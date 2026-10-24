using System.Linq.Expressions;
using Quacktion.App.Models;
using TaskAction = Quacktion.App.Models.Quacktion;
namespace Quacktion.App.Interfaces;

public interface IRepository<T> where T : Entity
{
    Task<T?> GetAsync(string id, CancellationToken ct);
    Task<T?> FindAsync(Expression<Func<T, bool>> filter, CancellationToken ct);
    Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> filter, int offset, int limit, CancellationToken ct);
    Task InsertAsync(T value, CancellationToken ct);
    Task<bool> ReplaceAsync(T value, CancellationToken ct);
    Task<bool> DeleteAsync(T value, CancellationToken ct);
}
public interface IAccountRepo : IRepository<Account>;
public interface IRefreshTokenRepo : IRepository<RefreshToken>;
public interface IDeviceRepo : IRepository<Device>;
public interface IPairingChallengeRepo : IRepository<PairingChallenge>;
public interface ISessionRepo : IRepository<Session>
{
    Task<Session?> ConsumeOwnedConnectionAsync(string sessionId, string ownerId, DateTime now, CancellationToken ct);
}
public interface ITranscriptRepo : IRepository<TranscriptEntry>;
public interface IAgentRepo : IRepository<Agent>;
public interface IQuacktionRepo : IRepository<TaskAction>
{
    Task<TaskAction?> ClaimAsync(DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> RenewAsync(string id, string leaseId, DateTime until, CancellationToken ct);
    Task<bool> FinishAsync(TaskAction action, string leaseId, CancellationToken ct);
    Task SyncAuditAsync(TaskAction action, CancellationToken ct);
}
