using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed class ElevenLabsSettings
{
    public string? ApiKey { get; set; }
    public string? AgentId { get; set; }
    public bool UseSignedUrl { get; set; } = true;
}

public sealed class QuacktionAgentGateway : IAgentGateway
{
    public string Provider => "quack";
    public bool Supports(string capability) => false;
    public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct) =>
        Task.FromResult(new AgentTaskResult(false, null, "quack_protocol_not_configured"));
}

public sealed class ElevenLabsService(
    IHttpClientFactory clients,
    IOptions<ElevenLabsSettings> settings,
    IConversationEventSink eventSink,
    ILogger<ElevenLabsService> logger
) : IElevenLabsService, IConversationalAgentGateway, IAudioProviderAdapter
{
    private const int MaxProviderMessageBytes = 1_048_576;
    private readonly ConcurrentDictionary<string, ElevenLabsConversation> byConversation = new();
    private readonly ConcurrentDictionary<string, ElevenLabsConversation> bySession = new();

    public async Task<IElevenLabsConversation> ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (bySession.ContainsKey(sessionId))
            throw new AppException(409, "conversation_already_connected");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            var uri = settings.Value.UseSignedUrl
                ? await GetSignedUrlAsync(timeout.Token)
                : PublicAgentUrl();
            await socket.ConnectAsync(uri, timeout.Token);
            var metadata = await ReceiveMetadataAsync(socket, timeout.Token);
            var conversation = new ElevenLabsConversation(
                sessionId, socket, metadata.ConversationId, metadata.InputFormat, metadata.OutputFormat, eventSink, logger,
                RemoveConversation
            );
            if (!bySession.TryAdd(sessionId, conversation) || !byConversation.TryAdd(conversation.ConversationId, conversation))
            {
                await conversation.DisposeAsync();
                throw new AppException(409, "conversation_already_connected");
            }
            return conversation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new AppException(504, "elevenlabs_connection_timeout");
        }
        catch (AppException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or JsonException or InvalidOperationException)
        {
            socket.Dispose();
            logger.LogWarning("ElevenLabs connection failed {ErrorType}", e.GetType().Name);
            throw new AppException(502, "elevenlabs_connection_failed");
        }
    }

    public async Task<AgentSessionResult> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken)
    {
        var conversation = await ConnectAsync(request.SessionId, cancellationToken);
        return new(conversation.ConversationId);
    }

    public Task SendUserTextAsync(string agentSessionId, string text, CancellationToken cancellationToken) =>
        GetConversation(agentSessionId).SendUserTextAsync(text, cancellationToken);

    public async Task EndSessionAsync(string agentSessionId, CancellationToken cancellationToken)
    {
        if (!byConversation.TryRemove(agentSessionId, out var conversation))
            return;
        RemoveSessionMapping(conversation);
        await conversation.CloseAsync(cancellationToken);
        await conversation.DisposeAsync();
    }

    public Task NegotiateAsync(string agentSessionId, AudioNegotiation negotiation, CancellationToken ct)
    {
        var conversation = GetConversation(agentSessionId);
        ValidateFormat(negotiation.Input, conversation.UserInputAudioFormat, "unsupported_input_audio_format");
        ValidateFormat(negotiation.Output, conversation.AgentOutputAudioFormat, "unsupported_output_audio_format");
        Guard.Input(negotiation.ProtocolVersion == 1, "unsupported_protocol_version");
        conversation.AudioNegotiated = true;
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(string agentSessionId, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var conversation = GetConversation(agentSessionId);
        Guard.State(conversation.AudioNegotiated, "audio_not_negotiated");
        Guard.Input(frame.Length is > 0 and <= 65536, "invalid_audio_frame");
        return conversation.SendAudioAsync(frame, ct);
    }

    public async IAsyncEnumerable<ProviderEvent> ReadEventsAsync(
        string agentSessionId,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var conversation = GetConversation(agentSessionId);
        await conversation.AcquireReaderAsync(ct);
        try
        {
            conversation.DiscardQueuedEvents();
            await foreach (var item in conversation.ReceiveEventsAsync(ct))
            {
                if (item.Type == "audio")
                {
                    yield return new("Audio", Audio: item.Audio);
                }
                else if (item.Type == "interruption")
                {
                    yield return new("Interruption");
                }
                else if (item.Type is "user_transcript" or "agent_response" && item.Text is { Length: > 0 } text)
                {
                    var speaker = item.Type == "user_transcript" ? "user" : "agent";
                    var eventId = item.EventId ?? Guid.NewGuid().ToString("N");
                    var transcript = new TranscriptEntry
                    {
                        SpeakerIdentifier = speaker,
                        Text = text,
                        Source = "elevenlabs",
                        SourceEventId = $"{item.Type}:{conversation.ConversationId}:{eventId}"
                    };
                    yield return new(item.Type, Transcript: transcript);
                }
                else if (item.Type == "disconnected")
                {
                    yield return new("Disconnected");
                    yield break;
                }
                else if (item.Type == "error")
                {
                    yield return new("Error", Transcript: null);
                }
            }
        }
        finally { conversation.ReleaseReader(); }
    }

    private ElevenLabsConversation GetConversation(string id) =>
        byConversation.TryGetValue(id, out var conversation)
            ? conversation
            : throw new AppException(409, "conversation_not_active");

    private void RemoveSessionMapping(ElevenLabsConversation conversation)
    {
        foreach (var pair in bySession)
            if (ReferenceEquals(pair.Value, conversation))
                bySession.TryRemove(pair.Key, out _);
    }

    private void RemoveConversation(string sessionId, ElevenLabsConversation conversation)
    {
        if (bySession.TryGetValue(sessionId, out var bySessionValue) && ReferenceEquals(bySessionValue, conversation))
            bySession.TryRemove(sessionId, out _);
        if (byConversation.TryGetValue(conversation.ConversationId, out var byConversationValue) && ReferenceEquals(byConversationValue, conversation))
            byConversation.TryRemove(conversation.ConversationId, out _);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(settings.Value.AgentId))
            throw new AppException(503, "elevenlabs_agent_not_configured");
        if (settings.Value.UseSignedUrl && string.IsNullOrWhiteSpace(settings.Value.ApiKey))
            throw new AppException(503, "elevenlabs_api_key_not_configured");
    }

    private Uri PublicAgentUrl() => new(
        "wss://api.elevenlabs.io/v1/convai/conversation?agent_id=" + Uri.EscapeDataString(settings.Value.AgentId!)
    );

    private async Task<Uri> GetSignedUrlAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "v1/convai/conversation/get-signed-url?agent_id=" + Uri.EscapeDataString(settings.Value.AgentId!)
        );
        request.Headers.Add("xi-api-key", settings.Value.ApiKey);
        using var response = await clients.CreateClient("ElevenLabs").SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new AppException(502, "elevenlabs_authentication_failed");
        if (!response.IsSuccessStatusCode)
            throw new AppException(502, "elevenlabs_signed_url_failed");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("signed_url", out var value)
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme != "wss"
            || uri.Host != "api.elevenlabs.io")
            throw new AppException(502, "elevenlabs_invalid_signed_url");
        return uri;
    }

    private static async Task<(string ConversationId, string InputFormat, string OutputFormat)> ReceiveMetadataAsync(
        ClientWebSocket socket,
        CancellationToken ct
    )
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (message.Length < MaxProviderMessageBytes)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new AppException(502, "elevenlabs_closed_before_ready");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new AppException(502, "elevenlabs_invalid_metadata");
            using var json = JsonDocument.Parse(message.ToArray());
            if (!json.RootElement.TryGetProperty("type", out var type)
                || type.GetString() != "conversation_initiation_metadata"
                || !json.RootElement.TryGetProperty("conversation_initiation_metadata_event", out var metadata))
                throw new AppException(502, "elevenlabs_invalid_metadata");
            var conversationId = metadata.GetProperty("conversation_id").GetString();
            var input = metadata.GetProperty("user_input_audio_format").GetString();
            var output = metadata.GetProperty("agent_output_audio_format").GetString();
            if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
                throw new AppException(502, "elevenlabs_invalid_metadata");
            return (conversationId, input, output);
        }
        throw new AppException(502, "elevenlabs_metadata_too_large");
    }

    private static void ValidateFormat(AudioFormat requested, string providerFormat, string error)
    {
        var expected = providerFormat switch
        {
            "pcm_16000" => (16000, "pcm_16000"),
            "pcm_22050" => (22050, "pcm_22050"),
            "pcm_24000" => (24000, "pcm_24000"),
            "pcm_44100" => (44100, "pcm_44100"),
            "pcm_48000" => (48000, "pcm_48000"),
            "ulaw_8000" => (8000, "ulaw_8000"),
            _ => throw new AppException(400, error)
        };
        Guard.Input(requested.SampleRate == expected.Item1 && requested.Channels == 1
            && string.Equals(requested.Codec, expected.Item2, StringComparison.OrdinalIgnoreCase), error);
    }

    private sealed class ElevenLabsConversation : IElevenLabsConversation
    {
        private readonly string sessionId;
        private readonly ClientWebSocket socket;
        private readonly string conversationId;
        private readonly ILogger logger;
        private readonly IConversationEventSink eventSink;
        private readonly Action<string, ElevenLabsConversation> onClosed;
        private readonly CancellationTokenSource lifetime = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly SemaphoreSlim readerLock = new(1, 1);
        private readonly Channel<ElevenLabsEvent> events = Channel.CreateBounded<ElevenLabsEvent>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true }
        );
        private readonly Task receiveLoop;
        public string ConversationId => conversationId;
        public string UserInputAudioFormat { get; }
        public string AgentOutputAudioFormat { get; }
        public bool AudioNegotiated { get; set; }

        public ElevenLabsConversation(
            string sessionId,
            ClientWebSocket socket,
            string conversationId,
            string userInputAudioFormat,
            string agentOutputAudioFormat,
            IConversationEventSink eventSink,
            ILogger logger,
            Action<string, ElevenLabsConversation> onClosed
        )
        {
            this.sessionId = sessionId;
            this.socket = socket;
            this.conversationId = conversationId;
            UserInputAudioFormat = userInputAudioFormat;
            AgentOutputAudioFormat = agentOutputAudioFormat;
            this.eventSink = eventSink;
            this.logger = logger;
            this.onClosed = onClosed;
            receiveLoop = Task.Run(() => PumpAsync(lifetime.Token));
        }

        public Task SendUserTextAsync(string text, CancellationToken cancellationToken = default) =>
            SendJsonAsync(new { type = "user_message", text }, cancellationToken);

        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default) =>
            SendJsonAsync(new { user_audio_chunk = Convert.ToBase64String(audio.Span) }, cancellationToken);

        public Task SendPongAsync(long eventId, CancellationToken cancellationToken = default) =>
            SendJsonAsync(new { type = "pong", event_id = eventId }, cancellationToken);

        public IAsyncEnumerable<ElevenLabsEvent> ReceiveEventsAsync(CancellationToken cancellationToken = default) =>
            events.Reader.ReadAllAsync(cancellationToken);

        public void DiscardQueuedEvents()
        {
            while (events.Reader.TryRead(out _)) { }
        }
        public Task AcquireReaderAsync(CancellationToken ct) => readerLock.WaitAsync(ct);
        public void ReleaseReader() => readerLock.Release();

        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session_ended", cancellationToken);
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            }
            await lifetime.CancelAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync(CancellationToken.None);
            try { await receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or WebSocketException) { }
            events.Writer.TryComplete();
            await readerLock.WaitAsync();
            readerLock.Release();
            socket.Dispose();
            lifetime.Dispose();
            sendLock.Dispose();
            readerLock.Dispose();
        }

        private async Task SendJsonAsync(object value, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await sendLock.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open)
                    throw new AppException(502, "elevenlabs_connection_closed");
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch (WebSocketException)
            {
                throw new AppException(502, "elevenlabs_send_failed");
            }
            finally { sendLock.Release(); }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await PublishAsync(new("disconnected"), CancellationToken.None);
                            return;
                        }
                        if (message.Length + result.Count > MaxProviderMessageBytes)
                            throw new AppException(502, "elevenlabs_message_too_large");
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;
                    await ProcessEventAsync(message.ToArray(), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("ElevenLabs receive failed {ConversationId} {ErrorType}", conversationId, e.GetType().Name);
                try { await PublishAsync(new("disconnected", ErrorCode: "provider_disconnected"), CancellationToken.None); }
                catch (ChannelClosedException) { }
            }
            finally
            {
                events.Writer.TryComplete();
                onClosed(sessionId, this);
            }
        }

        private async Task ProcessEventAsync(byte[] bytes, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var typeValue))
                return;
            var type = typeValue.GetString();
            if (type == "ping" && root.TryGetProperty("ping_event", out var ping))
            {
                var id = ping.GetProperty("event_id").GetInt64();
                var delay = ping.TryGetProperty("ping_ms", out var pingMs) ? pingMs.GetInt32() : 0;
                if (delay > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 5000)), ct);
                await SendPongAsync(id, ct);
                await PublishAsync(new("ping", PingEventId: id), ct);
                return;
            }
            if (type == "audio" && root.TryGetProperty("audio_event", out var audio))
            {
                var data = audio.GetProperty("audio_base_64").GetString();
                if (!string.IsNullOrWhiteSpace(data))
                    await PublishAsync(new("audio", Audio: Convert.FromBase64String(data)), ct);
                return;
            }
            if (type == "interruption")
            {
                await PublishAsync(new("interruption", EventId: NumberId(root)), ct);
                return;
            }
            if (type == "user_transcript" && root.TryGetProperty("user_transcription_event", out var user))
            {
                await PublishAsync(new("user_transcript", NumberId(root) ?? NumberId(user), user.GetProperty("user_transcript").GetString()), ct);
                return;
            }
            if (type == "agent_response" && root.TryGetProperty("agent_response_event", out var agent))
            {
                await PublishAsync(new("agent_response", NumberId(root) ?? NumberId(agent), agent.GetProperty("agent_response").GetString()), ct);
                return;
            }
            if (type == "client_error" || type == "error")
            {
                await PublishAsync(new("error", ErrorCode: "elevenlabs_provider_error"), ct);
                logger.LogWarning("ElevenLabs provider returned an error for {ConversationId}", conversationId);
            }
        }

        private async Task PublishAsync(ElevenLabsEvent item, CancellationToken ct)
        {
            item = item with { ConversationId = conversationId };
            events.Writer.TryWrite(item);
            await eventSink.HandleAsync(sessionId, conversationId, item, ct);
        }

        private static string? NumberId(JsonElement element) =>
            element.TryGetProperty("event_id", out var id) ? id.ToString() : null;
    }
}
