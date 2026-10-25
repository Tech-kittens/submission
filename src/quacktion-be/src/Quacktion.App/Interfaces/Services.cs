using Quacktion.App.DTOs;
using Quacktion.App.Models;
using TaskAction = Quacktion.App.Models.Quacktion;
namespace Quacktion.App.Interfaces;

public interface IAccountService
{
    Task<Account> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<Account> GetAsync(string owner, CancellationToken ct);
    Task<Account> UpdateAsync(string owner, ProfileRequest request, CancellationToken ct);
    Task DeleteAsync(string owner, CancellationToken ct);
    Task ChangePasswordAsync(string owner, PasswordRequest request, CancellationToken ct);
    Task<TokenResult> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<TokenResult> RefreshAsync(string token, CancellationToken ct);
    Task LogoutAsync(string token, CancellationToken ct);
}
public interface ITokenIssuer { (string Token, DateTime ExpiresAt) Issue(Account account); }
public interface IDeviceService
{
    Task<object> CreateAsync(string owner, DeviceRequest request, CancellationToken ct);
    Task<Device> GetAsync(string owner, string id, CancellationToken ct);
    Task<IReadOnlyList<Device>> ListAsync(string owner, int offset, int limit, CancellationToken ct);
    Task<Device> UpdateAsync(string owner, string id, DeviceUpdate request, CancellationToken ct);
    Task<object> ChallengeAsync(string owner, string id, CancellationToken ct);
    Task<object> PairAsync(string owner, string id, PairRequest request, CancellationToken ct);
    Task UnpairAsync(string owner, string id, bool delete, CancellationToken ct);
}
public interface IDeviceAuthenticationService
{
    Task<Device?> AuthenticateAsync(string id, string credential, CancellationToken ct);
    Task<Device?> AuthenticateCredentialAsync(string credential, CancellationToken ct);
}
public interface ISessionService
{
    Task<Session> SelectResumeAsync(string owner, string id, CancellationToken ct);
    Task<Session> ResumeAsync(string owner, string id, CancellationToken ct);
    Task SuspendAsync(string owner, string id, string? expectedAgentId, CancellationToken ct);
    Task<Session> CreateAsync(string owner, SessionRequest request, CancellationToken ct);
    Task<Session> StartNewAsync(string owner, SessionRequest request, string requestId, CancellationToken ct);
    Task<SessionAudioStartResult> CreateAudioSessionAsync(string owner, string? deviceId, string requestId, string name, int connectionWindowSeconds, CancellationToken ct);
    Task<Session> ConnectAccountSessionAsync(string sessionId, string owner, string? deviceId, CancellationToken ct);
    Task<Session> GetAsync(string owner, string id, CancellationToken ct);
    Task<IReadOnlyList<Session>> ListAsync(string owner, int offset, int limit, CancellationToken ct);
    Task<Session> UpdateAsync(string owner, string id, SessionUpdate request, CancellationToken ct);
    Task DeleteAsync(string owner, string id, CancellationToken ct);
    Task<Session> StartAsync(string owner, string id, CancellationToken ct);
    Task<Session> StartAsync(string owner, string id, string? initialPrompt, string promptFormat, string? requestId, CancellationToken ct);
    Task<Session> EndAsync(string owner, string id, CancellationToken ct, string? requestId = null);
    Task<Session> FailAsync(string owner, string id, string failureCode, CancellationToken ct);
    Task<bool> ExpireReadyAsync(string owner, string id, CancellationToken ct);
}
public interface IQuacktionService
{
    Task<TaskAction> ProposeAsync(string owner, string session, ProposalRequest request, CancellationToken ct);
    Task<TaskAction> ProposeAuthorizedMcpAsync(string owner, string session, ProposalRequest request,
        string connectionId, string[] services, CancellationToken ct);
    Task<TaskAction> GetAsync(string owner, string id, CancellationToken ct);
    Task<IReadOnlyList<TaskAction>> ListAsync(string owner, string? session, int offset, int limit, CancellationToken ct);
    Task<TaskAction> CancelAsync(string owner, string id, CancellationToken ct);
    Task<TaskAction> RetryAsync(string owner, string id, CancellationToken ct);
}
public interface IApprovalService { Task<TaskAction> ResolveAsync(string owner, string id, string digest, bool approve, CancellationToken ct); }
public interface IActionRiskEvaluator { bool IsSafe(string actionType); }
public interface IQuacktionExecutionService { Task<bool> ExecuteNextAsync(CancellationToken ct); }
public interface ITranscriptService
{
    Task<IReadOnlyList<TranscriptEntry>> ListAsync(string owner, string session, int offset, int limit, CancellationToken ct);
    Task AppendAsync(TranscriptEntry entry, CancellationToken ct);
}
public interface IResearchService { Task<ResearchResult> ResearchAsync(string query, CancellationToken ct); }
public interface IClaimVerificationService { Task<ClaimVerificationResult> VerifyAsync(string claim, CancellationToken ct); }
public interface ISessionEvents { Task PublishAsync(string owner, string session, string eventName, object payload, CancellationToken ct); }
public interface IAgentCatalog { Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct); }
public sealed record McpProviderInfo(string Id, string DisplayName, string[] Scopes);
public sealed record McpConnectionInfo(string Id, string Provider, string ProviderAccountId, string ServerId,
    string? DisplayName, IReadOnlyList<string> GrantedScopes, IReadOnlyList<string> EnabledTools, string Status, DateTime CreatedAt, IReadOnlyList<string>? EnabledServices = null, bool AccountIdentityVerified = false);
public sealed record McpToolInfo(string Name, string? Description, System.Text.Json.JsonElement InputSchema, bool ReadOnly, bool Destructive, string ServerId = "", string SchemaDigest = "");
public sealed record McpToolSelection(string[] EnabledTools);
public sealed record McpTaskAccountSelection(string[] Services);
public sealed record McpToolInvocation(string OwnerAccountId, string SessionId, string ConnectionId,
    string ToolName, System.Text.Json.JsonElement Arguments, string ServerId = "", string ParentTaskId = "",
    string? SkillId = null, string? ApprovalContext = null, bool ForceApproval = false);
public interface IMcpToolGateway
{
    Task<System.Text.Json.JsonElement> ExecuteReadOnlyAsync(McpToolInvocation invocation, CancellationToken ct);
    Task<System.Text.Json.JsonElement> ExecuteOrProposeAsync(McpToolInvocation invocation, CancellationToken ct);
}
public interface IMcpSkillRegistry
{
    Task<IReadOnlyList<McpSkillDefinition>> GetAvailableSkillsAsync(string userId, CancellationToken cancellationToken);
    Task<McpSkillDefinition?> GetSkillAsync(string skillId, CancellationToken cancellationToken);
    Task<IReadOnlyList<McpSkillDefinition>> GetSkillsForConnectionAsync(string userId, string connectionId, CancellationToken cancellationToken);
}
public interface IMcpSkillExecutor
{
    Task<SkillExecutionResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken);
    Task<SkillExecutionResult> ResumeAsync(string executionId, string userId, string parentTaskId,
        CancellationToken cancellationToken);
}
public interface IMcpConnectionService
{
    Task<IReadOnlyList<McpConnectionInfo>> ListAsync(string owner, CancellationToken ct);
    IReadOnlyList<McpProviderInfo> Providers();
    Task HealthAsync(string owner, string connectionId, CancellationToken ct);
    Task<TaskAction> BindTaskAsync(string owner, string connectionId, string taskId, string[] services, CancellationToken ct);
    Task<Uri> BeginConnectAsync(string owner, string serverId, CancellationToken ct);
    Task<McpConnectionInfo> CompleteConnectAsync(string code, string state, CancellationToken ct);
    Task DisconnectAsync(string owner, string connectionId, CancellationToken ct);
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(string owner, string connectionId, CancellationToken ct);
    Task<McpConnectionInfo> SetEnabledToolsAsync(string owner, string connectionId, string[] toolNames, CancellationToken ct);
}
