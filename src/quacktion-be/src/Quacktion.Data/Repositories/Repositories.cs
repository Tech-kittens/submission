using System.Linq.Expressions;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Repositories;

public class MongoRepo<T>(IMongoDbContext context) : IRepository<T> where T : Entity
{
    protected IMongoDbContext Context { get; } = context;
    protected IMongoCollection<T> Collection { get; } = context.Collection<T>();
    public Task<T?> GetAsync(string id, CancellationToken ct) => FindAsync(x => x.Id == id, ct);
    public async Task<T?> FindAsync(Expression<Func<T, bool>> filter, CancellationToken ct) => await Collection.Find(filter).FirstOrDefaultAsync(ct);
    public async Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> filter, int offset, int limit, CancellationToken ct) => await Collection.Find(filter).SortBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Limit(limit).ToListAsync(ct);
    public async Task InsertAsync(T value, CancellationToken ct)
    { try { await Collection.InsertOneAsync(value, cancellationToken: ct); } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw new AppException(409, "duplicate_key"); } }
    public async Task<bool> ReplaceAsync(T value, CancellationToken ct)
    {
        var version = value.Version; value.Version++;
        try
        {
            var result = await Collection.ReplaceOneAsync(x => x.Id == value.Id && x.Version == version, value, cancellationToken: ct);
            if (result.ModifiedCount == 1) return true;
            value.Version = version; return false;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { value.Version = version; throw new AppException(409, "duplicate_key"); }
    }
    public async Task<bool> DeleteAsync(T value, CancellationToken ct) => (await Collection.DeleteOneAsync(x => x.Id == value.Id && x.Version == value.Version, ct)).DeletedCount == 1;
}
public sealed class AccountRepo(IMongoDbContext db) : MongoRepo<Account>(db), IAccountRepo;
public sealed class RefreshTokenRepo(IMongoDbContext db) : MongoRepo<RefreshToken>(db), IRefreshTokenRepo;
public sealed class DeviceRepo(IMongoDbContext db) : MongoRepo<Device>(db), IDeviceRepo;
public sealed class PairingChallengeRepo(IMongoDbContext db) : MongoRepo<PairingChallenge>(db), IPairingChallengeRepo;
public sealed class SessionRepo(IMongoDbContext db) : MongoRepo<Session>(db), ISessionRepo
{
    public async Task<Session?> ConsumeOwnedConnectionAsync(string sessionId, string ownerId, string? deviceId, DateTime now, CancellationToken ct)
    {
        var filter = Builders<Session>.Filter.Eq(x => x.Id, sessionId)
            & Builders<Session>.Filter.Eq(x => x.OwnerAccountId, ownerId)
            & Builders<Session>.Filter.Eq(x => x.Status, SessionStatus.Ready)
            & Builders<Session>.Filter.Gt(x => x.ConnectionCredentialExpiresAt, now)
            & Builders<Session>.Filter.Eq(x => x.ConnectionCredentialConsumedAt, null);
        if (deviceId is not null)
            filter &= Builders<Session>.Filter.Eq(x => x.DeviceId, deviceId);
        var update = Builders<Session>.Update
            .Set(x => x.Status, SessionStatus.Connecting)
            .Set(x => x.ConnectionCredentialConsumedAt, now)
            .Set(x => x.UpdatedAt, now)
            .Inc(x => x.Version, 1);
        return await Collection.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<Session> { ReturnDocument = ReturnDocument.After }, ct);
    }
}
public sealed class TranscriptRepo(IMongoDbContext db) : MongoRepo<TranscriptEntry>(db), ITranscriptRepo
{
    public async Task<IReadOnlyList<TranscriptEntry>> RecentAsync(string sessionId, int limit, CancellationToken ct)
    {
        var recent = await Collection.Find(x => x.SessionId == sessionId).SortByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Limit(limit).ToListAsync(ct);
        recent.Reverse();
        return recent;
    }
}
public sealed class AgentRepo(IMongoDbContext db) : MongoRepo<Agent>(db), IAgentRepo;
public sealed class RealtimeStateRepo(IMongoDbContext db, Quacktion.Data.Sessions.HandleCipher cipher) : IRealtimeStateRepo
{
    private readonly MongoRepo<RealtimeState> inner = new(db);
    private RealtimeState? Decode(RealtimeState? state)
    {
        if (state is not null) state.ResumptionHandle = cipher.Decrypt(state.EncryptedResumptionHandle, state.OwnerAccountId, state.SessionId);
        return state;
    }
    public async Task<RealtimeState?> GetAsync(string id, CancellationToken ct) => Decode(await inner.GetAsync(id, ct));
    public async Task<RealtimeState?> FindAsync(Expression<Func<RealtimeState, bool>> filter, CancellationToken ct) => Decode(await inner.FindAsync(filter, ct));
    public async Task<IReadOnlyList<RealtimeState>> ListAsync(Expression<Func<RealtimeState, bool>> filter, int offset, int limit, CancellationToken ct) => (await inner.ListAsync(filter, offset, limit, ct)).Select(x => Decode(x)!).ToArray();
    public async Task InsertAsync(RealtimeState value, CancellationToken ct)
    {
        var handle = value.ResumptionHandle;
        value.EncryptedResumptionHandle = cipher.Encrypt(handle, value.OwnerAccountId, value.SessionId); value.ResumptionHandle = null;
        try { await inner.InsertAsync(value, ct); } finally { value.ResumptionHandle = handle; }
    }
    public async Task<bool> ReplaceAsync(RealtimeState value, CancellationToken ct)
    {
        var handle = value.ResumptionHandle;
        value.EncryptedResumptionHandle = cipher.Encrypt(handle, value.OwnerAccountId, value.SessionId); value.ResumptionHandle = null;
        try { return await inner.ReplaceAsync(value, ct); } finally { value.ResumptionHandle = handle; }
    }
    public Task<bool> DeleteAsync(RealtimeState value, CancellationToken ct) => inner.DeleteAsync(value, ct);
}
public sealed class MeetingToolCallRepo(IMongoDbContext db) : MongoRepo<MeetingToolCall>(db), IMeetingToolCallRepo;
public sealed class UserMcpConnectionRepo(IMongoDbContext db) : MongoRepo<UserMcpConnection>(db), IUserMcpConnectionRepo;
public sealed class McpCredentialRepo(IMongoDbContext db) : MongoRepo<McpCredential>(db), IMcpCredentialRepo;
public sealed class McpOAuthTransactionRepo(IMongoDbContext db) : MongoRepo<McpOAuthTransaction>(db), IMcpOAuthTransactionRepo;
public sealed class McpExecutionAuditRepo(IMongoDbContext db) : MongoRepo<McpExecutionAudit>(db), IMcpExecutionAuditRepo;

public sealed class McpServerRegistrationRepo(IMongoDbContext db) : MongoRepo<McpServerRegistration>(db), IMcpServerRegistrationRepo;
