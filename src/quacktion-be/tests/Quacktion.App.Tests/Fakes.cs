using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using TaskAction = Quacktion.App.Models.Quacktion;
namespace Quacktion.App.Tests;

public class MemoryRepo<T> : IRepository<T> where T : Entity
{
    protected readonly Dictionary<string, T> Values = [];
    protected readonly object Gate = new();
    protected static T Clone(T value) => (T)CloneEntity(value);
    private static Entity CloneEntity(Entity entity)
    {
        // JSON ignores secrets for HTTP; use a field/property clone retaining secret fields in the fake repository.
        var copy = (Entity)Activator.CreateInstance(entity.GetType())!;
        foreach (var property in entity.GetType().GetProperties()) property.SetValue(copy, property.GetValue(entity));
        if (copy is TaskAction q && entity is TaskAction original)
        { q.History = original.History.Select(x => (QuacktionExecution)CloneEntity(x)).ToList(); q.Approval = original.Approval is null ? null : (ApprovalRequest)CloneEntity(original.Approval); }
        if (copy is RefreshToken token && entity is RefreshToken source) token.UsedHashes = [.. source.UsedHashes];
        return copy;
    }
    public Task<T?> GetAsync(string id, CancellationToken ct) { lock (Gate) return Task.FromResult(Values.TryGetValue(id, out var x) ? Clone(x) : null); }
    public Task<T?> FindAsync(Expression<Func<T, bool>> filter, CancellationToken ct) { lock (Gate) { var x = Values.Values.FirstOrDefault(filter.Compile()); return Task.FromResult(x is null ? null : Clone(x)); } }
    public Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> filter, int offset, int limit, CancellationToken ct) { lock (Gate) return Task.FromResult<IReadOnlyList<T>>(Values.Values.Where(filter.Compile()).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(limit).Select(Clone).ToList()); }
    public Task InsertAsync(T value, CancellationToken ct)
    {
        lock (Gate)
        {
            if (value is Account a && Values.Values.Cast<Account>().Any(x => x.Email == a.Email)
                || value is Device d && Values.Values.Cast<Device>().Any(x => x.DeviceIdentifier == d.DeviceIdentifier)
                || value is TranscriptEntry t && Values.Values.Cast<TranscriptEntry>().Any(x => x.SessionId == t.SessionId && x.Source == t.Source && x.SourceEventId == t.SourceEventId))
                throw new AppException(409, "duplicate_key");
            if (Values.ContainsKey(value.Id)) throw new AppException(409, "duplicate_key");
            Values.Add(value.Id, Clone(value)); return Task.CompletedTask;
        }
    }
    public Task<bool> ReplaceAsync(T value, CancellationToken ct)
    { lock (Gate) { if (!Values.TryGetValue(value.Id, out var old) || old.Version != value.Version) return Task.FromResult(false); value.Version++; Values[value.Id] = Clone(value); return Task.FromResult(true); } }
    public Task<bool> DeleteAsync(T value, CancellationToken ct) { lock (Gate) return Task.FromResult(Values.TryGetValue(value.Id, out var old) && old.Version == value.Version && Values.Remove(value.Id)); }
}
public sealed class Accounts : MemoryRepo<Account>, IAccountRepo;
public sealed class Tokens : MemoryRepo<RefreshToken>, IRefreshTokenRepo;
public sealed class Devices : MemoryRepo<Device>, IDeviceRepo;
public sealed class Challenges : MemoryRepo<PairingChallenge>, IPairingChallengeRepo;
public sealed class Sessions : MemoryRepo<Session>, ISessionRepo
{
    public Task<Session?> ConsumeOwnedConnectionAsync(string sessionId, string ownerId, string? deviceId, DateTime now, CancellationToken ct)
    {
        lock (Gate)
        {
            if (!Values.TryGetValue(sessionId, out var value) || value.OwnerAccountId != ownerId || (deviceId is not null && value.DeviceId != deviceId)
                || value.Status != SessionStatus.Ready || value.ConnectionCredentialExpiresAt <= now || value.ConnectionCredentialConsumedAt is not null)
                return Task.FromResult<Session?>(null);
            value.Status = SessionStatus.Connecting; value.ConnectionCredentialConsumedAt = now; value.UpdatedAt = now; value.Version++;
            return Task.FromResult<Session?>(Clone(value));
        }
    }
}
public sealed class Transcripts : MemoryRepo<TranscriptEntry>, ITranscriptRepo
{
    public Task<IReadOnlyList<TranscriptEntry>> RecentAsync(string sessionId, int limit, CancellationToken ct)
    {
        lock (Gate) return Task.FromResult<IReadOnlyList<TranscriptEntry>>(Values.Values.Where(x => x.SessionId == sessionId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).TakeLast(limit).Select(Clone).ToList());
    }
}
public sealed class Agents : MemoryRepo<Agent>, IAgentRepo;
public sealed class McpConnections : MemoryRepo<UserMcpConnection>, IUserMcpConnectionRepo;
public sealed class McpCredentials : MemoryRepo<McpCredential>, IMcpCredentialRepo;
public sealed class McpTransactions : MemoryRepo<McpOAuthTransaction>, IMcpOAuthTransactionRepo;
public sealed class McpAudits : MemoryRepo<McpExecutionAudit>, IMcpExecutionAuditRepo;
public sealed class Actions : MemoryRepo<TaskAction>, IQuacktionRepo
{
    public Task<TaskAction?> ClaimAsync(DateTime now, TimeSpan lease, CancellationToken ct)
    {
        lock (Gate)
        {
            var q = Values.Values.FirstOrDefault(x => x.Attempts < 5 && (x.Status == QuacktionStatus.Queued && x.NextAttemptAt <= now || x.Status == QuacktionStatus.InProgress && x.LeaseExpiresAt <= now));
            if (q is null) return Task.FromResult<TaskAction?>(null);
            q.Status = QuacktionStatus.InProgress; q.LeaseId = Guid.NewGuid().ToString(); q.LeaseExpiresAt = now + lease; q.Attempts++; q.Version++;
            q.History.Add(new() { QuacktionId = q.Id, LeaseId = q.LeaseId, Attempt = q.Attempts }); return Task.FromResult<TaskAction?>(Clone(q));
        }
    }
    public Task<bool> RenewAsync(string id, string leaseId, DateTime until, CancellationToken ct)
    { lock (Gate) { var q = Values[id]; if (q.LeaseId != leaseId || q.LeaseExpiresAt <= DateTime.UtcNow) return Task.FromResult(false); q.LeaseExpiresAt = until; q.Version++; return Task.FromResult(true); } }
    public Task<bool> FinishAsync(TaskAction action, string leaseId, CancellationToken ct)
    {
        lock (Gate)
        {
            var q = Values[action.Id]; if (q.LeaseId != leaseId || q.LeaseExpiresAt <= DateTime.UtcNow) return Task.FromResult(false);
            if (q.CancellationRequested) action.Status = QuacktionStatus.Cancelled;
            action.Version = q.Version + 1; action.LeaseId = null; action.LeaseExpiresAt = null; Values[action.Id] = Clone(action); return Task.FromResult(true);
        }
    }
    public Task SyncAuditAsync(TaskAction action, CancellationToken ct) => Task.CompletedTask;
}
public sealed class Events : ISessionEvents
{
    public List<string> Names { get; } = [];
    public Task PublishAsync(string owner, string session, string eventName, object payload, CancellationToken ct) { lock (Names) Names.Add(eventName); return Task.CompletedTask; }
}
public sealed class Connections : IDeviceConnectionManager
{
    public bool IsConnected(string id) => false;
    public Task DisconnectAsync(string id, CancellationToken ct) => Task.CompletedTask;
    public Task SendBinaryAsync(string id, ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
    public Task SendTextAsync(string id, ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
}
public sealed class Issuer : ITokenIssuer { public (string Token, DateTime ExpiresAt) Issue(Account a) => ("test_access", DateTime.UtcNow.AddMinutes(10)); }
public sealed class Voice : IConversationalAgentGateway
{
    public bool Fail;
    public Func<string, CancellationToken, Task>? OnEnd;
    public List<string> UserTexts { get; } = [];
    public Task<AgentSessionResult> StartSessionAsync(AgentSessionRequest request, CancellationToken ct) => Fail ? throw new AppException(503, "unavailable") : Task.FromResult(new AgentSessionResult("voice-" + request.SessionId));
    public Task SendUserTextAsync(string id, string text, CancellationToken ct) { UserTexts.Add(text); return Task.CompletedTask; }
    public Task EndSessionAsync(string id, CancellationToken ct) => OnEnd?.Invoke(id, ct) ?? Task.CompletedTask;
}
public sealed class TaskAgent : IAgentOrchestrator
{
    public Func<AgentTaskRequest, CancellationToken, Task<AgentTaskResult>> Run { get; set; } = (_, _) => Task.FromResult(new AgentTaskResult(true, "{}", null));
    public int Calls;
    public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct) { Interlocked.Increment(ref Calls); return Run(request, ct); }
}
public sealed class Fixture
{
    public Accounts Accounts { get; } = new(); public Tokens Tokens { get; } = new(); public Devices Devices { get; } = new(); public Challenges Challenges { get; } = new();
    public Sessions Sessions { get; } = new(); public Transcripts Transcripts { get; } = new(); public Actions Actions { get; } = new(); public Voice Voice { get; } = new(); public Events Events { get; } = new(); public Connections Connections { get; } = new(); public TaskAgent Agent { get; } = new();
    public AccountService AccountService => new(Accounts, Tokens, new PasswordHasher<Account>(), new Issuer(), NullLogger<AccountService>.Instance);
    public DeviceService DeviceService => new(Devices, Challenges, AccountService, Connections, NullLogger<DeviceService>.Instance);
    public SessionService SessionService => new(Sessions, Devices, Voice, Connections, Events, NullLogger<SessionService>.Instance);
    public QuacktionService ActionService => new(Actions, SessionService, new ActionRiskEvaluator(), Events);
    public ApprovalService Approvals => new(ActionService, Actions, Events, NullLogger<ApprovalService>.Instance);
    public QuacktionExecutionService Executor => new(Actions, Accounts, new ActionRiskEvaluator(), Agent, Events, NullLogger<QuacktionExecutionService>.Instance);
    public async Task<(Account Account, Session Session)> Seed()
    { var a = await AccountService.RegisterAsync(new("user@example.test", "a-secure-password", "User"), default); var s = await SessionService.CreateAsync(a.Id, new("Meeting", null), default); await SessionService.StartAsync(a.Id, s.Id, default); return (a, s); }
}
