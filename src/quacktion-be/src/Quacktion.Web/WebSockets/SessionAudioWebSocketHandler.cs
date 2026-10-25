using System.Net.WebSockets;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Audio;

namespace Quacktion.Web.WebSockets;

public interface ISessionAudioWebSocketHandler
{
    Task HandleAsync(HttpContext context, string sessionId, string owner, string? deviceId);
}

public sealed class SessionAudioWebSocketHandler(
    ISessionService sessions,
    ISessionRepo sessionRepo,
    IDeviceRepo devices,
    IAudioStreamCoordinator audio,
    ISessionAudioConnectionManager audioConnections,
    IHostApplicationLifetime host,
    ILogger<SessionAudioWebSocketHandler> logger
) : ISessionAudioWebSocketHandler
{
    private sealed record InputFrame(byte[] Data, long ReceivedAt);
    private const int MaxMessageBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record AudioStartControl(int Version, string Type, AudioNegotiation? Audio);

    public async Task HandleAsync(HttpContext context, string sessionId, string owner, string? deviceId)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync("quacktion.audio.v1");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        var ct = lifetime.Token;
        Session session;
        try
        {
            session = await sessions.ConnectAccountSessionAsync(sessionId, owner, deviceId, ct);
        }
        catch (Exception e)
        {
            var code = e is AppException app ? app.Code : "session_connection_failed";
            var pending = await sessionRepo.GetAsync(sessionId, CancellationToken.None);
            await TrySendControlAsync(socket, new { version = 1, type = "session.start_failed", requestId = pending?.StartRequestId, payload = new { sessionId, code } }, CancellationToken.None);
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "session_start_failed", CancellationToken.None); }
            catch (WebSocketException) { }
            return;
        }

        var device = deviceId is null ? null : await devices.GetAsync(deviceId, ct);
        if (deviceId is not null && device is null)
        {
            await sessions.EndAsync(session.OwnerAccountId, session.Id, CancellationToken.None);
            return;
        }

        var outgoing = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        using var audioConnection = audioConnections.Register(session.Id, async (data, token) =>
        {
            var frame = new OutboundFrame(data.ToArray(), WebSocketMessageType.Text, new(TaskCreationOptions.RunContinuationsAsynchronously));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
            await outgoing.Writer.WriteAsync(frame, linked.Token);
            await frame.Sent!.Task.WaitAsync(linked.Token);
            using var control = JsonDocument.Parse(data);
            if (control.RootElement.TryGetProperty("type", out var type) && type.GetString() is "session.ended" or "session.failed" or "session.suspended")
                lifetime.Cancel();
        });
        var input = Channel.CreateBounded<InputFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var sender = SendLoopAsync(socket, outgoing.Reader, logger, session.Id, ct);
        Task? providerSender = null;
        Task? providerReceiver = null;
        try
        {
            await outgoing.Writer.WriteAsync(ControlFrame(new { version = 1, type = "session.started", requestId = session.StartRequestId, payload = new { sessionId = session.Id, status = session.Status.ToString() } }), ct);
            using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var first = await ReceiveMessageAsync(socket, setupTimeout.Token);
            Guard.Input(first.Type == WebSocketMessageType.Text, "audio_start_required");
            var start = JsonSerializer.Deserialize<AudioStartControl>(first.Data, Json) ?? throw new AppException(400, "invalid_audio_control");
            Guard.Input(start.Version == 1, "unsupported_protocol_version");
            Guard.Input(start.Type is "audio.start" or "AudioStart", "audio_start_required");
            Guard.Input(start.Audio is not null, "audio_format_required");
            var providerSessionId = device is null
                ? await audio.AttachAsync(session.OwnerAccountId, session.Id, start.Audio!, ct)
                : await audio.AttachAsync(device, session.Id, start.Audio!, ct);
            await outgoing.Writer.WriteAsync(ControlFrame(new { version = 1, type = "audio.ready", audio = start.Audio }), ct);
            providerSender = SendInputLoopAsync(providerSessionId, input.Reader, ct);
            providerReceiver = ReceiveProviderLoopAsync(providerSessionId, session, outgoing, ct);

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message.Type == WebSocketMessageType.Close) break;
                if (message.Type == WebSocketMessageType.Binary)
                {
                    await input.Writer.WriteAsync(new(message.Data, Stopwatch.GetTimestamp()), ct);
                    continue;
                }

                using var controlJson = JsonDocument.Parse(message.Data);
                var root = controlJson.RootElement;
                Guard.Input(root.TryGetProperty("version", out var versionNode) && versionNode.GetInt32() == 1, "unsupported_protocol_version");
                var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                if (type is "session.end" or "EndSession")
                {
                    var requestId = root.TryGetProperty("requestId", out var requestIdNode) ? requestIdNode.GetString() : null;
                    await sessions.EndAsync(session.OwnerAccountId, session.Id, ct, requestId);
                    break;
                }
                throw new AppException(400, "unsupported_audio_control");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (Exception e)
        {
            var code = e is AppException app ? app.Code : "audio_proxy_failed";
            logger.LogWarning("Session audio proxy failed {SessionId} {Code}", session.Id, code);
            try
            {
                using var failureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sessions.SuspendAsync(session.OwnerAccountId, session.Id, session.AgentSessionId, failureTimeout.Token);
            }
            catch (Exception sendError) when (sendError is ChannelClosedException or TimeoutException or WebSocketException or OperationCanceledException) { }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var current = await sessionRepo.GetAsync(session.Id, cleanup.Token);
                if (current?.Status == SessionStatus.Active)
                    await sessions.SuspendAsync(session.OwnerAccountId, session.Id, session.AgentSessionId, cleanup.Token);
            }
            catch (Exception e) when (e is AppException or OperationCanceledException)
            { logger.LogDebug("Session close raced with provider state {SessionId}", session.Id); }
            await lifetime.CancelAsync();
            input.Writer.TryComplete();
            outgoing.Writer.TryComplete();
            await ObserveAsync(providerSender);
            await ObserveAsync(providerReceiver);
            await ObserveAsync(sender);

            if (socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session_ended", close.Token); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            }
        }

        async Task SendInputLoopAsync(string agentSessionId, ChannelReader<InputFrame> frames, CancellationToken token)
        {
            var forwardedFrames = 0;
            long forwardedBytes = 0;
            try
            {
                await foreach (var frame in frames.ReadAllAsync(token))
                {
                    await audio.SendAsync(agentSessionId, frame.Data, token);
                    forwardedFrames++;
                    forwardedBytes += frame.Data.Length;
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("VoiceTiming {SessionId} {Stage} {ElapsedMs:F2}", session.Id,
                            "audio_forward", Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds);
                    if (forwardedFrames == 1 || forwardedFrames % 100 == 0)
                        logger.LogInformation("Forwarded microphone audio {SessionId} {FrameCount} {ByteCount}", session.Id, forwardedFrames, forwardedBytes);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("Session audio input failed {SessionId} {ErrorType}", session.Id, e.GetType().Name);
                await FailTransport("audio_input_failed");
                lifetime.Cancel();
            }
        }

        async Task ReceiveProviderLoopAsync(string agentSessionId, Session activeSession, Channel<OutboundFrame> queue, CancellationToken token)
        {
            try
            {
                await foreach (var item in audio.ReadAsync(agentSessionId, token))
                {
                    if (item.Type.Equals("Interruption", StringComparison.OrdinalIgnoreCase))
                    {
                        var controls = new List<OutboundFrame>(16);
                        while (queue.Reader.TryRead(out var queued))
                            if (queued.Type != WebSocketMessageType.Binary) controls.Add(queued);
                        foreach (var control in controls) await queue.Writer.WriteAsync(control, token);
                        await queue.Writer.WriteAsync(ControlFrame(new { version = 1, type = "audio.clear" }), token);
                    }
                    if (item.Audio is { Length: > 0 } bytes)
                    {
                        Guard.Input(bytes.Length <= MaxMessageBytes, "provider_frame_too_large");
                        await queue.Writer.WriteAsync(new(bytes, WebSocketMessageType.Binary,
                            PlaybackCancellation: item.PlaybackCancellation, TurnId: item.TurnId,
                            ReceivedAt: item.ReceivedAt), token);
                    }
                    if (item.Type == "Disconnected")
                    {
                        await sessions.SuspendAsync(activeSession.OwnerAccountId, activeSession.Id, activeSession.AgentSessionId, token);
                        lifetime.Cancel();
                        return;
                    }
                }
                var current = await sessionRepo.GetAsync(activeSession.Id, token);
                if (current?.Status == SessionStatus.Active)
                    await sessions.SuspendAsync(activeSession.OwnerAccountId, activeSession.Id, activeSession.AgentSessionId, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("Session audio output failed {SessionId} {ErrorType}", activeSession.Id, e.GetType().Name);
                await FailTransport("audio_output_failed");
                lifetime.Cancel();
            }
        }

        async Task FailTransport(string code)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await sessions.SuspendAsync(session.OwnerAccountId, session.Id, session.AgentSessionId, timeout.Token); }
            catch (Exception e) when (e is AppException or OperationCanceledException) { }
        }
    }

    private static OutboundFrame ControlFrame(object value) =>
        new(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text);

    private static async Task TrySendControlAsync(WebSocket socket, object value, CancellationToken ct)
    {
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text, true, ct); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, []);
            if (message.Length + result.Count > MaxMessageBytes) throw new AppException(400, "message_too_large");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, message.ToArray());
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<OutboundFrame> frames,
        ILogger logger, string sessionId, CancellationToken ct)
    {
        string? firstTurnSent = null;
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
            {
                if (frame.PlaybackCancellation.IsCancellationRequested) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.SendAsync(frame.Data, frame.Type, true, timeout.Token);
                if (frame.TurnId is { } turnId && turnId != firstTurnSent)
                {
                    firstTurnSent = turnId;
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("VoiceTiming {SessionId} {TurnId} {Stage} {ElapsedMs:F2}", sessionId, turnId,
                            "provider_audio_to_client_send", frame.ReceivedAt == 0 ? -1 : Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds);
                }
                frame.Sent?.TrySetResult();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ChannelClosedException)
        {
            while (frames.TryRead(out var pending)) pending.Sent?.TrySetException(e);
            throw;
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or WebSocketException or ChannelClosedException) { }
    }
}
