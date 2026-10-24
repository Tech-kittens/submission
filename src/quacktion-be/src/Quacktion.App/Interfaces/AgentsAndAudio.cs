using Quacktion.App.Models;
namespace Quacktion.App.Interfaces;

public record AgentSessionRequest(string SessionId, string OwnerAccountId, string? InitialPrompt = null);
public record AgentSessionResult(string AgentSessionId);
public interface IConversationalAgentGateway
{
    Task<AgentSessionResult> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken);
    Task SendUserTextAsync(string agentSessionId, string text, CancellationToken cancellationToken);
    Task EndSessionAsync(string agentSessionId, CancellationToken cancellationToken);
}
public record ElevenLabsEvent(
    string Type,
    string? EventId = null,
    string? Text = null,
    byte[]? Audio = null,
    string? ConversationId = null,
    long? PingEventId = null,
    string? ErrorCode = null
);
public interface IElevenLabsConversation : IAsyncDisposable
{
    string ConversationId { get; }
    Task SendUserTextAsync(string text, CancellationToken cancellationToken = default);
    Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default);
    Task SendPongAsync(long eventId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ElevenLabsEvent> ReceiveEventsAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
public interface IElevenLabsService
{
    Task<IElevenLabsConversation> ConnectAsync(string sessionId, CancellationToken cancellationToken = default);
}
public interface IConversationEventSink
{
    Task HandleAsync(string sessionId, string conversationId, ElevenLabsEvent item, CancellationToken cancellationToken);
}
public record AgentTaskRequest(string ActionType, string ParametersJson, string IdempotencyKey, string OwnerAccountId, string SessionId);
public record AgentTaskResult(bool Success, string? ResultJson, string? FailureCode, bool Transient = false);
public interface IAgentGateway
{
    string Provider { get; }
    bool Supports(string capability);
    Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct);
}
public interface IAgentOrchestrator { Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct); }
public record AudioFormat(string Codec, int SampleRate, int Channels);
public record AudioNegotiation(int ProtocolVersion, AudioFormat Input, AudioFormat Output);
public record ProviderEvent(string Type, byte[]? Audio = null, TranscriptEntry? Transcript = null);
public interface IAudioProviderAdapter
{
    Task NegotiateAsync(string agentSessionId, AudioNegotiation negotiation, CancellationToken ct);
    Task SendAudioAsync(string agentSessionId, ReadOnlyMemory<byte> frame, CancellationToken ct);
    IAsyncEnumerable<ProviderEvent> ReadEventsAsync(string agentSessionId, CancellationToken ct);
}
public interface IDeviceConnectionManager
{
    bool IsConnected(string deviceId);
    Task DisconnectAsync(string deviceId, CancellationToken ct);
    Task SendBinaryAsync(string deviceId, ReadOnlyMemory<byte> data, CancellationToken ct);
    Task SendTextAsync(string deviceId, ReadOnlyMemory<byte> data, CancellationToken ct);
}
public interface ISessionAudioConnectionManager
{
    IDisposable Register(string sessionId, Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendControl);
    Task<bool> SendControlAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken ct);
}
public interface IAudioStreamCoordinator
{
    Task<string> AttachAsync(Device device, string sessionId, AudioNegotiation negotiation, CancellationToken ct);
    Task SendAsync(string agentSessionId, ReadOnlyMemory<byte> frame, CancellationToken ct);
    IAsyncEnumerable<ProviderEvent> ReadAsync(string agentSessionId, CancellationToken ct);
}
