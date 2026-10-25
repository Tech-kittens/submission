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
public record AgentTaskRequest(string ActionType, string ParametersJson, string IdempotencyKey, string OwnerAccountId, string SessionId);
public record AgentTaskResult(bool Success, string? ResultJson, string? FailureCode, bool Transient = false, IReadOnlyList<string>? PendingTaskIds = null);
public interface IAgentGateway
{
    string Provider { get; }
    bool Supports(string capability);
    Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct);
}
public sealed record AgentRouteCandidate(string Provider, string[] Capabilities);
public sealed record AgentRouteRequest(string ActionType, string ParametersJson, IReadOnlyList<AgentRouteCandidate> Candidates);
public sealed record AgentRouteDecision(string Provider, string Intent, double Confidence);
public interface IJevSkillRouter
{
    Task<string?> SuggestSkillAsync(string query, IReadOnlyList<Quacktion.App.Models.McpSkillDefinition> available,
        CancellationToken ct);
}
public interface IAgentRouter
{
    Task<AgentRouteDecision?> RouteAsync(AgentRouteRequest request, CancellationToken ct);
}
public interface IAgentOrchestrator { Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct); }
public record AudioFormat(string Codec, int SampleRate, int Channels);
public record AudioNegotiation(int ProtocolVersion, AudioFormat Input, AudioFormat Output);
public record ProviderEvent(string Type, byte[]? Audio = null, TranscriptEntry? Transcript = null,
    CancellationToken PlaybackCancellation = default, string? TurnId = null, long ReceivedAt = 0);
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
    Task<string> AttachAsync(string owner, string sessionId, AudioNegotiation negotiation, CancellationToken ct);
    Task SendAsync(string agentSessionId, ReadOnlyMemory<byte> frame, CancellationToken ct);
    IAsyncEnumerable<ProviderEvent> ReadAsync(string agentSessionId, CancellationToken ct);
}

public interface IConversationAttachmentGateway
{
    bool IsActive(string agentSessionId);
}
