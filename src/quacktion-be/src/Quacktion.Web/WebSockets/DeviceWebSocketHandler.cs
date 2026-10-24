using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Audio;
namespace Quacktion.Web.WebSockets;

public interface IDeviceWebSocketHandler { Task HandleAsync(HttpContext context, Device device, string credential); }
public sealed record DeviceControl(int Version, string Type, string? SessionId = null, AudioNegotiation? Audio = null,
    string? RequestId = null, JsonElement Payload = default);
public sealed class DeviceWebSocketHandler(DeviceConnectionManager connections, IDeviceAuthenticationService authentication,
    IDeviceRepo devices, ISessionRepo sessions, ISessionService lifecycle, IAudioStreamCoordinator audio, ITranscriptService transcripts,
    ISessionEvents events, IHostApplicationLifetime host, ILogger<DeviceWebSocketHandler> logger) : IDeviceWebSocketHandler
{
    private sealed record IncomingAudio(string AgentId, string SessionId, CancellationToken StreamToken, byte[] Data);
    private const int MaxMessage = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task HandleAsync(HttpContext context, Device device, string credential)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync("quacktion.v1");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        using var connection = connections.Register(device.Id, socket, lifetime.Token);
        var ct = connection.Lifetime.Token;
        var incoming = Channel.CreateBounded<IncomingAudio>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
        string? agent = null; string? sessionId = null; CancellationTokenSource? streamLifetime = null; Task? provider = null;
        var sender = SendLoop(connection, ct);
        var watchdog = Watchdog();
        var consumer = ConsumeAudio();
        try
        {
            if (await authentication.AuthenticateAsync(device.Id, credential, ct) is null) throw new AppException(401, "device_credential_revoked");
            await Presence(DeviceStatus.Online, ct);
            await Control(new { Version = 1, Type = "DeviceConnected", DeviceId = device.Id, MaxMessageBytes = MaxMessage });
            logger.LogInformation("DeviceConnected {DeviceId} {ConnectionId}", device.Id, connection.Id);
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
                    Guard.State(agent is not null && streamLifetime is not null && !streamLifetime.IsCancellationRequested, "audio_not_started");
                    await incoming.Writer.WriteAsync(new(agent!, sessionId!, streamLifetime!.Token, message.ToArray()), timeout.Token);
                    continue;
                }
                var control = JsonSerializer.Deserialize<DeviceControl>(message.ToArray(), Json) ?? throw new AppException(400, "invalid_control");
                Guard.Input(control.Version == 1, "unsupported_protocol_version");
                switch (control.Type)
                {
                    case "heartbeat":
                    case "Heartbeat":
                        await Presence(DeviceStatus.Online, ct);
                        if (control.Type == "Heartbeat") await Control(new { Version = 1, Type = "Heartbeat" });
                        else await Control(new { Version = 1, Type = "heartbeat" });
                        break;
                    case "session.start":
                        {
                            string? requestId = control.RequestId;
                            try
                            {
                                var payload = control.Payload;
                                var prompt = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("initialPrompt", out var promptNode) ? promptNode.GetString() : null;
                                var format = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("promptFormat", out var formatNode) ? formatNode.GetString() ?? "text" : "text";
                                Guard.Input(!string.IsNullOrWhiteSpace(requestId), "request_id_required");
                                Guard.Input(format is "text" or "audio", "unsupported_prompt_format");
                                var started = await lifecycle.StartNewAsync(device.OwnerAccountId,
                                    new Quacktion.App.DTOs.SessionRequest("Wake word session", device.Id, prompt, format), requestId!, ct);
                                sessionId = started.Id;
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
                        Guard.State(s.Status == SessionStatus.Active); sessionId = s.Id;
                        await Control(new { Version = 1, Type = "StartSession", SessionId = s.Id }); break;
                    case "audio.start":
                    case "AudioStart":
                        Guard.State(agent is null && sessionId is not null, "invalid_audio_state");
                        Guard.Input(control.Audio is not null, "audio_format_required");
                        agent = await audio.AttachAsync(device, sessionId!, control.Audio!, ct);
                        streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        provider = ReceiveProvider(agent, streamLifetime.Token);
                        if (control.Type == "AudioStart") await Control(new { Version = 1, Type = "AudioStart", control.Audio });
                        else await Control(new { Version = 1, Type = "audio.started", control.Audio });
                        break;
                    case "audio.stop":
                    case "AudioStop":
                        await StopAudio();
                        if (control.Type == "AudioStop") await Control(new { Version = 1, Type = "AudioStop" });
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
        async Task Control(object value) => await connection.Outgoing.Writer.WriteAsync(new(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text), ct);
        async Task ConsumeAudio()
        {
            try
            {
                await foreach (var frame in incoming.Reader.ReadAllAsync(ct))
                {
                    if (frame.StreamToken.IsCancellationRequested) continue;
                    var s = await sessions.GetAsync(frame.SessionId, ct);
                    if (s?.Status != SessionStatus.Active) { connection.Stop(); return; }
                    try { await audio.SendAsync(frame.AgentId, frame.Data, frame.StreamToken); }
                    catch (OperationCanceledException) when (frame.StreamToken.IsCancellationRequested && !ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { connection.Stop(); }
        }
        async Task ReceiveProvider(string target, CancellationToken token)
        {
            try
            {
                await foreach (var item in audio.ReadAsync(target, token))
                {
                    if (item.Audio is { } bytes)
                    {
                        Guard.Input(bytes.Length <= MaxMessage, "provider_frame_too_large");
                        await connection.Outgoing.Writer.WriteAsync(new(bytes, WebSocketMessageType.Binary), token);
                    }
                    if (item.Transcript is { } transcript)
                    {
                        transcript.SessionId = sessionId!;
                        await transcripts.AppendAsync(transcript, token);
                    }
                    if (item.Type == "Disconnected")
                    {
                        await events.PublishAsync(device.OwnerAccountId, sessionId!, "AgentStatusChanged", new { Status = "Disconnected" }, token);
                        connection.Stop();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { connection.Stop(); }
        }
        async Task StopAudio()
        {
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
                    if (await authentication.AuthenticateAsync(device.Id, credential, ct) is null) { connection.Stop(); return; }
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
    private static async Task SendLoop(DeviceConnection connection, CancellationToken ct)
    {
        try { await foreach (var frame in connection.Outgoing.Reader.ReadAllAsync(ct)) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10)); await connection.Socket.SendAsync(frame.Data, frame.Type, true, timeout.Token); } }
        catch { connection.Stop(); }
    }
}
