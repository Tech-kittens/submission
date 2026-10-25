using System.Net.WebSockets;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Audio;
using Quacktion.Data.Preparation;
namespace Quacktion.Web.WebSockets;

public interface IDeviceWebSocketHandler { Task HandleAsync(HttpContext context, Device device, string credential); }
public sealed record DeviceControl(int Version, string Type, string? SessionId = null, AudioNegotiation? Audio = null,
    string? RequestId = null, JsonElement Payload = default);
public sealed class DeviceWebSocketHandler(DeviceConnectionManager connections, IDeviceAuthenticationService authentication,
    IDeviceRepo devices, ISessionRepo sessions, ISessionService lifecycle, IAudioStreamCoordinator audio, ITranscriptService transcripts,
    DeviceMeetingStream meeting, IHostApplicationLifetime host, ILogger<DeviceWebSocketHandler> logger, IDeviceTransportPersistence? transport = null, IConversationPersistence? conversations = null, IConversationAttachmentGateway? localConversations = null, DeviceNotificationService? notifications = null) : IDeviceWebSocketHandler
{
    private sealed record IncomingAudio(string AgentId, string SessionId, CancellationToken StreamToken, byte[] Data, long ReceivedAt);
    private const int MaxMessage = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task HandleAsync(HttpContext context, Device device, string credential)
    {
        if (await authentication.AuthenticateAsync(device.Id, credential, context.RequestAborted) is null)
            throw new AppException(401, "device_credential_revoked");
        var transportLease = Guid.NewGuid().ToString("N");
        using var socket = await context.WebSockets.AcceptWebSocketAsync("quacktion.v1");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        using var connection = connections.Register(device.Id, socket, lifetime.Token);
        var ct = connection.Lifetime.Token;
        if (transport is not null)
        {
            try { await transport.AcquireAsync(device, transportLease, connections.InstanceId, ct); }
            catch (AppException e) when (e.Code == "device_connection_in_use")
            {
                var rejected = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "session.start_failed", payload = new { code = e.Code } }, Json);
                try { await socket.SendAsync(rejected, WebSocketMessageType.Text, true, context.RequestAborted); } catch (WebSocketException) { }
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, e.Code, context.RequestAborted); } catch (WebSocketException) { }
                connections.Remove(device.Id, connection);
                return;
            }
        }
        meeting.Initialize(device);
        var incoming = Channel.CreateBounded<IncomingAudio>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
        string? agent = null; string? sessionId = null; string? conversationAgent = null; bool audioReturned = false; CancellationTokenSource? streamLifetime = null; Task? provider = null;
        var sender = SendLoop(connection, logger, device.Id, ct);
        var watchdog = Watchdog();
        var consumer = ConsumeAudio();
        try
        {
            if (await authentication.AuthenticateAsync(device.Id, credential, ct) is null) throw new AppException(401, "device_credential_revoked");
            await Presence(DeviceStatus.Online, ct);
            await LegacyControl(new { Version = 1, Type = "DeviceConnected", DeviceId = device.Id, MaxMessageBytes = MaxMessage });
            logger.LogInformation("DeviceConnected {DeviceId} {ConnectionId}", device.Id, connection.Id);
            // A meeting reconnect still needs the live conversation attached: meeting PCM is
            // transcribed in parallel and also forwarded to Gemini when a session exists.
            if (device.ActiveSessionId is { } resumeSessionId && !meeting.MutedByUser)
            {
                var previous = await sessions.GetAsync(resumeSessionId, ct);
                if (previous?.OwnerAccountId == device.OwnerAccountId && previous.DeviceId == device.Id && previous.EndedAt is null &&
                    (previous.Status == SessionStatus.Suspended || previous.Status == SessionStatus.Active &&
                        (previous.AgentSessionId is not null && localConversations?.IsActive(previous.AgentSessionId) == true ||
                         conversations is not null && previous.UpdatedAt <= DateTime.UtcNow.AddSeconds(-75) &&
                         !await conversations.IsLeasedAsync(device.OwnerAccountId, resumeSessionId, ct))))
                {
                    try
                    {
                        var restored = previous.Status == SessionStatus.Active && previous.AgentSessionId is not null && localConversations?.IsActive(previous.AgentSessionId) == true
                            ? previous : await lifecycle.ResumeAsync(device.OwnerAccountId, resumeSessionId, ct);
                        sessionId = restored.Id; conversationAgent = restored.AgentSessionId;
                        if (meeting.UsesMeetingAudio)
                        {
                            var pcm16 = new AudioFormat("pcm_16000", 16000, 1);
                            agent = await audio.AttachAsync(device, sessionId, new AudioNegotiation(1, pcm16, pcm16), ct);
                            connection.AudioActive = true;
                            connection.SessionId = sessionId;
                            streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            provider = ReceiveProvider(agent, sessionId, streamLifetime.Token);
                        }
                        await Control(new { version = 1, type = "session.started", requestId = restored.StartRequestId, payload = new { sessionId, status = "Active" } });
                    }
                    catch (AppException e) { logger.LogInformation("Automatic resume deferred {SessionId} {Code}", resumeSessionId, e.Code); }
                }
            }
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested && connections.IsCurrent(device.Id, connection))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var message = new MemoryStream(); WebSocketReceiveResult result; WebSocketMessageType? type = null;
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (type is not null && type != result.MessageType) throw new AppException(400, "mixed_message_type");
                    type = result.MessageType;
                    if (message.Length + result.Count > MaxMessage) throw new AppException(400, "message_too_large");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (type == WebSocketMessageType.Binary)
                {
                    if (meeting.UsesMeetingAudio)
                    {
                        var frame = message.ToArray();
                        try {
                            meeting.Audio(frame);
                            if (!audioReturned && transport is not null)
                            {
                                audioReturned = true;
                                _ = Task.Run(async () =>
                                {
                                    try { await transport.AudioReturnedAsync(device, transportLease, CancellationToken.None); }
                                    catch (Exception error) { logger.LogWarning("Recording gap close deferred {DeviceId} {ErrorType}", device.Id, error.GetType().Name); }
                                }, CancellationToken.None);
                            }
                        }
                        catch (AppException e) { await Control(new { version = 1, type = "error", code = e.Code }); }
                        if (agent is not null && streamLifetime is not null && !streamLifetime.IsCancellationRequested)
                        {
                            // The same device PCM frame feeds Live without a second device stream.
                            if (!incoming.Writer.TryWrite(new(agent, sessionId!, streamLifetime.Token, frame, Stopwatch.GetTimestamp())))
                                await Control(new { version = 1, type = "error", code = "live_audio_queue_full" });
                        }
                        continue;
                    }
                    Guard.State(agent is not null && streamLifetime is not null && !streamLifetime.IsCancellationRequested, "audio_not_started");
                    // Waiting for queue capacity is backpressure, not part of the receive deadline.
                    await incoming.Writer.WriteAsync(new(agent!, sessionId!, streamLifetime!.Token, message.ToArray(), Stopwatch.GetTimestamp()), ct);
                    continue;
                }
                using var controlJson = JsonDocument.Parse(message.ToArray());
                var root = controlJson.RootElement;
                var name = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                if (name == "notify.done" && notifications is not null)
                {
                    try { await notifications.AcknowledgeAsync(device, connection.Id, root, ct); }
                    catch (Exception e) when (e is AppException or JsonException or InvalidOperationException or FormatException)
                    { await Control(new { version = 1, type = "error", code = e is AppException app ? app.Code : "invalid_notification_ack" }); }
                    continue;
                }
                if (DeviceMeetingStream.Handles(name))
                {
                    try
                    {
                        if (sessionId is { } previousId)
                        {
                            var previous = await sessions.GetAsync(previousId, ct);
                            if (previous is null || previous.Status is SessionStatus.Completed or SessionStatus.Failed)
                            { await StopAudio(); sessionId = null; }
                        }
                        await meeting.HandleAsync(root, agent is not null, Control, ct);
                        if (meeting.MutedByUser && sessionId is { } mutedSession)
                        {
                            await StopAudio();
                            await lifecycle.EndAsync(device.OwnerAccountId, mutedSession, ct);
                            sessionId = null;
                        }
                    }
                    catch (Exception e) when (e is AppException or JsonException or InvalidOperationException or FormatException)
                    { await Control(new { version = 1, type = "error", code = e is AppException app ? app.Code : "invalid_control" }); }
                    continue;
                }
                var control = JsonSerializer.Deserialize<DeviceControl>(message.ToArray(), Json) ?? throw new AppException(400, "invalid_control");
                Guard.Input(control.Version == 1, "unsupported_protocol_version");
                switch (control.Type)
                {
                    case "heartbeat":
                    case "Heartbeat":
                        await Presence(DeviceStatus.Online, ct);
                        if (control.Type == "Heartbeat") await LegacyControl(new { Version = 1, Type = "Heartbeat" });
                        else await Control(new { version = 1, type = "heartbeat" });
                        break;
                    case "session.start":
                        {
                            string? requestId = control.RequestId;
                            try
                            {
                                Guard.State(!meeting.UsesMeetingAudio, "use_session_audio_socket");
                                Guard.State(!meeting.MutedByUser, "muted_by_user");
                                if (sessionId is not null)
                                {
                                    var prior = await sessions.GetAsync(sessionId, ct);
                                    if (prior is null || prior.Status is SessionStatus.Completed or SessionStatus.Failed)
                                    { await StopAudio(); sessionId = null; }
                                }
                                var payload = control.Payload;
                                var prompt = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("initialPrompt", out var promptNode) ? promptNode.GetString() : null;
                                var format = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("promptFormat", out var formatNode) ? formatNode.GetString() ?? "text" : "text";
                                Guard.Input(!string.IsNullOrWhiteSpace(requestId), "request_id_required");
                                Guard.Input(format is "text" or "audio", "unsupported_prompt_format");
                                var currentDevice = await devices.GetAsync(device.Id, ct);
                                var resumable = currentDevice?.ActiveSessionId is { } existingId ? await sessions.GetAsync(existingId, ct) : null;
                                var started = resumable?.OwnerAccountId == device.OwnerAccountId && resumable.DeviceId == device.Id &&
                                    resumable.EndedAt is null && (resumable.Status == SessionStatus.Suspended || resumable.Status == SessionStatus.Active &&
                                        resumable.AgentSessionId is not null && localConversations?.IsActive(resumable.AgentSessionId) == true || resumable.Status == SessionStatus.Active && conversations is not null &&
                                        !await conversations.IsLeasedAsync(device.OwnerAccountId, resumable.Id, ct))
                                    ? (resumable.Status == SessionStatus.Active && resumable.AgentSessionId is not null && localConversations?.IsActive(resumable.AgentSessionId) == true
                                        ? resumable : await lifecycle.ResumeAsync(device.OwnerAccountId, resumable.Id, ct))
                                    : await lifecycle.StartNewAsync(device.OwnerAccountId,
                                    new Quacktion.App.DTOs.SessionRequest("Wake word session", device.Id, prompt, format), requestId!, ct);
                                sessionId = started.Id; conversationAgent = started.AgentSessionId;
                                await Control(new { version = 1, type = "session.started", requestId, payload = new { sessionId = started.Id, status = started.Status.ToString() } });
                            }
                            catch (Exception e) when (e is AppException or JsonException)
                            {
                                var code = e is AppException app ? app.Code : "invalid_control";
                                await Control(new { version = 1, type = "session.start_failed", requestId, payload = new { code } });
                            }
                            catch (Exception e)
                            {
                                logger.LogWarning("DeviceSessionStartFailed {DeviceId} {ErrorType}", device.Id, e.GetType().Name);
                                await Control(new { version = 1, type = "session.start_failed", requestId, payload = new { code = "session_start_failed" } });
                            }
                            break;
                        }
                    case "StartSession":
                        Guard.State(sessionId is null, "already_associated");
                        var s = Guard.Found(await sessions.GetAsync(control.SessionId ?? "", ct));
                        if (s.OwnerAccountId != device.OwnerAccountId || s.DeviceId != device.Id) throw new AppException(404, "not_found");
                        if (s.Status == SessionStatus.Created) s = await lifecycle.StartAsync(device.OwnerAccountId, s.Id, ct);
                        else if (s.Status != SessionStatus.Active || s.AgentSessionId is null || localConversations?.IsActive(s.AgentSessionId) != true)
                            s = await lifecycle.ResumeAsync(device.OwnerAccountId, s.Id, ct);
                        conversationAgent = s.AgentSessionId;
                        Guard.State(s.Status == SessionStatus.Active); sessionId = s.Id;
                        await LegacyControl(new { Version = 1, Type = "StartSession", SessionId = s.Id }); break;
                    case "audio.start":
                    case "AudioStart":
                        Guard.State(!meeting.UsesMeetingAudio, "use_session_audio_socket");
                        Guard.State(agent is null && sessionId is not null, "invalid_audio_state");
                        Guard.Input(control.Audio is not null, "audio_format_required");
                        agent = await audio.AttachAsync(device, sessionId!, control.Audio!, ct);
                        connection.AudioActive = true; connection.SessionId = sessionId;
                        streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        provider = ReceiveProvider(agent, sessionId!, streamLifetime.Token);
                        if (control.Type == "AudioStart") await LegacyControl(new { Version = 1, Type = "AudioStart", control.Audio });
                        else await Control(new { version = 1, type = "audio.ready", audio = control.Audio });
                        break;
                    case "audio.stop":
                    case "AudioStop":
                        await StopAudio();
                        if (control.Type == "AudioStop") await LegacyControl(new { Version = 1, Type = "AudioStop" });
                        else await Control(new { version = 1, type = "audio.stopped" });
                        break;
                    case "session.end":
                    case "EndSession":
                        Guard.State(sessionId is not null); await StopAudio();
                        var endingId = sessionId!;
                        await lifecycle.EndAsync(device.OwnerAccountId, endingId, ct, control.RequestId); sessionId = null; break;
                    default: throw new AppException(400, "unsupported_control");
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        catch (Exception e)
        {
            var code = e is AppException app ? app.Code : "transport_error";
            logger.LogWarning("DeviceTransportError {DeviceId} {Code}", device.Id, code);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await connection.Outgoing.Writer.WriteAsync(new(JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Type = "Error", Code = code }), WebSocketMessageType.Text), timeout.Token); await Task.Delay(30, timeout.Token); } catch (OperationCanceledException) { }
        }
        finally
        {
            using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try { if (sessionId is not null && connections.IsCurrent(device.Id, connection)) await lifecycle.SuspendAsync(device.OwnerAccountId, sessionId, conversationAgent, cleanup.Token); }
                catch (Exception e) { logger.LogWarning("Conversation suspension deferred {SessionId} {ErrorType}", sessionId, e.GetType().Name); }
                try { if (transport is not null) await transport.DisconnectAsync(device, transportLease, cleanup.Token); }
                catch (Exception e) { logger.LogWarning("Device checkpoint deferred {DeviceId} {ErrorType}", device.Id, e.GetType().Name); }
                try { await meeting.FinishAsync("disconnect", cleanup.Token); }
                catch (Exception e) { logger.LogWarning("Meeting segment cleanup failed {DeviceId} {ErrorType}", device.Id, e.GetType().Name); }
                try { await meeting.DrainAsync(cleanup.Token); }
                catch (Exception e) { logger.LogWarning("Meeting transcription drain failed {DeviceId} {ErrorType}", device.Id, e.GetType().Name); }
            }
            // Drain the sole send loop before writing a close frame: WebSocket permits only one concurrent sender.
            await StopAudio();
            connection.Outgoing.Writer.TryComplete();
            try { await sender.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { connection.Stop(); }
            // Graceful close is bounded; Abort also releases blocked sends/receives.
            if (socket.State == WebSocketState.Open)
            { using var close = new CancellationTokenSource(TimeSpan.FromSeconds(1)); try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "connection_ended", close.Token); } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { } }
            connection.Stop(); incoming.Writer.TryComplete();
            await StopAudio();
            try { await Task.WhenAll(sender, consumer, watchdog); } catch (Exception e) when (e is OperationCanceledException or WebSocketException or ChannelClosedException) { }
            if (connections.IsCurrent(device.Id, connection))
            { connections.Remove(device.Id, connection); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Presence(DeviceStatus.Offline, cleanup.Token); }
            logger.LogInformation("DeviceDisconnected {DeviceId} {ConnectionId}", device.Id, connection.Id);
        }
        async Task Control(object value) => await connection.Outgoing.Writer.WriteAsync(new(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text), ct);
        async Task LegacyControl(object value) => await connection.Outgoing.Writer.WriteAsync(new(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text), ct);
        async Task ConsumeAudio()
        {
            try
            {
                await foreach (var frame in incoming.Reader.ReadAllAsync(ct))
                {
                    if (frame.StreamToken.IsCancellationRequested) continue;
                    // The authenticated stream is bound to its active session at audio.start.
                    // audio.stop/session.end cancel StreamToken. A database read for every 20-40 ms
                    // PCM frame made network/database latency part of the critical audio path.
                    try
                    {
                        await audio.SendAsync(frame.AgentId, frame.Data, frame.StreamToken);
                        if (logger.IsEnabled(LogLevel.Debug))
                            logger.LogDebug("VoiceTiming {SessionId} {Stage} {ElapsedMs:F2}", frame.SessionId,
                                "audio_forward", Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds);
                    }
                    catch (OperationCanceledException) when (frame.StreamToken.IsCancellationRequested && !ct.IsCancellationRequested) { }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Device audio input failed {SessionId} {ErrorType}", frame.SessionId, e.GetType().Name);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await lifecycle.SuspendAsync(device.OwnerAccountId, frame.SessionId, frame.AgentId, cleanup.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { connection.Stop(); }
        }
        async Task ReceiveProvider(string target, string targetSession, CancellationToken token)
        {
            try
            {
                await foreach (var item in audio.ReadAsync(target, token))
                {
                    if (item.Type == "Interruption")
                        await Control(new { version = 1, type = "audio.clear" });
                    if (item.Audio is { } bytes)
                    {
                        Guard.Input(bytes.Length <= MaxMessage, "provider_frame_too_large");
                        await connection.Outgoing.Writer.WriteAsync(new(bytes, WebSocketMessageType.Binary,
                            PlaybackCancellation: item.PlaybackCancellation, TurnId: item.TurnId,
                            ReceivedAt: item.ReceivedAt), token);
                    }
                    if (item.Transcript is { } transcript)
                    {
                        transcript.SessionId = targetSession;
                        await transcripts.AppendAsync(transcript, token);
                    }
                    if (item.Type == "Disconnected")
                    {
                        await lifecycle.SuspendAsync(device.OwnerAccountId, targetSession, target, token);
                        await Control(new { version = 1, type = "session.suspended", payload = new { sessionId = targetSession, status = "Suspended", reason = "provider_disconnected" } });
                        return;
                    }
                }
                var current = await sessions.GetAsync(targetSession, token);
                if (current?.Status == SessionStatus.Active)
                    await lifecycle.SuspendAsync(device.OwnerAccountId, targetSession, target, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("Device provider failed {SessionId} {ErrorType}", targetSession, e.GetType().Name);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await lifecycle.SuspendAsync(device.OwnerAccountId, targetSession, target, cleanup.Token); }
                catch (Exception failure) { logger.LogWarning("Device session cleanup failed {SessionId} {ErrorType}", targetSession, failure.GetType().Name); }
            }
        }
        async Task StopAudio()
        {
            connection.AudioActive = false; connection.PendingBriefingNotificationId = null;
            agent = null;
            if (streamLifetime is not null) { await streamLifetime.CancelAsync(); if (provider is not null) await provider; streamLifetime.Dispose(); streamLifetime = null; }
            while (incoming.Reader.TryRead(out _)) { }
        }
        async Task Watchdog()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                while (await timer.WaitForNextTickAsync(ct))
                {
                    using var checkpoint = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    checkpoint.CancelAfter(TimeSpan.FromSeconds(5));
                    if (await authentication.AuthenticateAsync(device.Id, credential, checkpoint.Token) is null ||
                        transport is not null && !await transport.CheckpointAsync(device, transportLease, checkpoint.Token))
                    { connection.Stop(); return; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { connection.Stop(); }
        }
        async Task Presence(DeviceStatus status, CancellationToken token)
        {
            for (var i = 0; i < 3; i++)
            {
                var d = await devices.GetAsync(device.Id, token); if (d is null) return;
                if (status == DeviceStatus.Online && !connections.IsCurrent(device.Id, connection)) return;
                if (status == DeviceStatus.Offline && connections.IsConnected(device.Id)) return;
                d.Status = d.CredentialHash is null ? DeviceStatus.Unpaired : status; d.LastSeenAt = DateTime.UtcNow;
                if (await devices.ReplaceAsync(d, token)) return;
            }
        }
    }
    private static async Task SendLoop(DeviceConnection connection, ILogger logger, string deviceId, CancellationToken ct)
    {
        string? firstTurnSent = null;
        try { await foreach (var frame in connection.Outgoing.Reader.ReadAllAsync(ct)) { if (frame.PlaybackCancellation.IsCancellationRequested) continue; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10)); if (frame.Batch is { } batch)
            {
                foreach (var part in batch) await connection.Socket.SendAsync(part.Data, part.Type, true, timeout.Token);
            }
            else
            {
                await connection.Socket.SendAsync(frame.Data, frame.Type, true, timeout.Token);
                if (frame.Type == WebSocketMessageType.Binary) connection.LastAssistantAudioAt = DateTime.UtcNow;
            }
            frame.Sent?.TrySetResult();
            if (frame.TurnId is { } turnId && turnId != firstTurnSent)
            { firstTurnSent = turnId; if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("VoiceTiming {DeviceId} {TurnId} {Stage} {ElapsedMs:F2}", deviceId, turnId,
                    "provider_audio_to_device_send", frame.ReceivedAt == 0 ? -1 : Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds); }
        } }
        catch { connection.Stop(); }
    }
}
