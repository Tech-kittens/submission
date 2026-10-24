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
    Task<Session> CreateAsync(string owner, SessionRequest request, CancellationToken ct);
    Task<Session> StartNewAsync(string owner, SessionRequest request, string requestId, CancellationToken ct);
    Task<SessionAudioStartResult> CreateAudioSessionAsync(string deviceId, string requestId, string name, int connectionWindowSeconds, CancellationToken ct);
    Task<Session> ConnectAccountDeviceSessionAsync(string sessionId, string owner, CancellationToken ct);
    Task<Session> GetAsync(string owner, string id, CancellationToken ct);
    Task<IReadOnlyList<Session>> ListAsync(string owner, int offset, int limit, CancellationToken ct);
    Task<Session> UpdateAsync(string owner, string id, SessionUpdate request, CancellationToken ct);
    Task DeleteAsync(string owner, string id, CancellationToken ct);
    Task<Session> StartAsync(string owner, string id, CancellationToken ct);
    Task<Session> StartAsync(string owner, string id, string? initialPrompt, string promptFormat, string? requestId, CancellationToken ct);
    Task<Session> EndAsync(string owner, string id, CancellationToken ct, string? requestId = null);
    Task<Session> FailAsync(string owner, string id, string failureCode, CancellationToken ct);
}
public interface IQuacktionService
{
    Task<TaskAction> ProposeAsync(string owner, string session, ProposalRequest request, CancellationToken ct);
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
