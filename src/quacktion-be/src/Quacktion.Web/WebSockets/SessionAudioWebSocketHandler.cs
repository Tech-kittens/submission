using System.Net.WebSockets;
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
    Task HandleAsync(HttpContext context, string sessionId, string owner);
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
    private const int MaxMessageBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record AudioStartControl(int Version, string Type, AudioNegotiation? Audio);

    public async Task HandleAsync(HttpContext context, string sessionId, string owner)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync("quacktion.audio.v1");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        var ct = lifetime.Token;
        Session session;
        try
        {
            session = await sessions.ConnectAccountDeviceSessionAsync(sessionId, owner, ct);
            if (string.IsNullOrWhiteSpace(session.DeviceId)) throw new AppException(401, "session_device_missing");
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

        var device = await devices.GetAsync(session.DeviceId!, ct);
        if (device is null)
        {
            await sessions.EndAsync(session.OwnerAccountId, session.Id, CancellationToken.None);
            return;
        }

        var outgoing = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        using var audioConnection = audioConnections.Register(session.Id, async (data, token) =>
        {
            var frame = new OutboundFrame(data.ToArray(), WebSocketMessageType.Text, new(TaskCreationOptions.RunContinuationsAsynchronously));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
            await outgoing.Writer.WriteAsync(frame, linked.Token);
            await frame.Sent!.Task.WaitAsync(linked.Token);
        });
        var input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var sender = SendLoopAsync(socket, outgoing.Reader, ct);
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
            var providerSessionId = await audio.AttachAsync(device, session.Id, start.Audio!, ct);
            await outgoing.Writer.WriteAsync(ControlFrame(new { version = 1, type = "audio.ready", audio = start.Audio }), ct);
            providerSender = SendInputLoopAsync(providerSessionId, input.Reader, ct);
            providerReceiver = ReceiveProviderLoopAsync(providerSessionId, session, outgoing, ct);

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message.Type == WebSocketMessageType.Close) break;
                if (message.Type == WebSocketMessageType.Binary)
                {
                    await input.Writer.WriteAsync(message.Data, ct);
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
            var failure = new OutboundFrame(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "error", code }, Json), WebSocketMessageType.Text, new(TaskCreationOptions.RunContinuationsAsynchronously));
            try
            {
                await outgoing.Writer.WriteAsync(failure, CancellationToken.None);
                await failure.Sent!.Task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception sendError) when (sendError is ChannelClosedException or TimeoutException or WebSocketException or OperationCanceledException) { }
        }
        finally
        {
            await lifetime.CancelAsync();
            input.Writer.TryComplete();
            outgoing.Writer.TryComplete();
            await ObserveAsync(providerSender);
            await ObserveAsync(providerReceiver);
            try { await sender.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { socket.Abort(); }

            var current = await sessions.GetAsync(session.OwnerAccountId, session.Id, CancellationToken.None);
            if (current.Status == SessionStatus.Active)
            {
                try { await sessions.EndAsync(session.OwnerAccountId, session.Id, CancellationToken.None); }
                catch (AppException e) { logger.LogDebug("Session close raced with provider state {SessionId} {Code}", session.Id, e.Code); }
            }
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session_ended", CancellationToken.None); }
                catch (WebSocketException) { }
            }
        }

        async Task SendInputLoopAsync(string agentSessionId, ChannelReader<byte[]> frames, CancellationToken token)
        {
            try
            {
                await foreach (var frame in frames.ReadAllAsync(token))
                    await audio.SendAsync(agentSessionId, frame, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("Session audio input failed {SessionId} {ErrorType}", session.Id, e.GetType().Name);
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
                        await queue.Writer.WriteAsync(new(bytes, WebSocketMessageType.Binary), token);
                    }
                    if (item.Type == "Disconnected")
                    {
                        await sessions.FailAsync(activeSession.OwnerAccountId, activeSession.Id, "provider_disconnected", token);
                        var failed = new OutboundFrame(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "session.failed", payload = new { sessionId = activeSession.Id, status = "Failed" } }, Json), WebSocketMessageType.Text, new(TaskCreationOptions.RunContinuationsAsynchronously));
                        await queue.Writer.WriteAsync(failed, token);
                        await failed.Sent!.Task.WaitAsync(token);
                        lifetime.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("Session audio output failed {SessionId} {ErrorType}", activeSession.Id, e.GetType().Name);
                lifetime.Cancel();
            }
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

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<OutboundFrame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
            {
                await socket.SendAsync(frame.Data, frame.Type, true, ct);
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
