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
    public async Task<Session?> ConsumeOwnedConnectionAsync(string sessionId, string ownerId, DateTime now, CancellationToken ct)
    {
        var filter = Builders<Session>.Filter.Eq(x => x.Id, sessionId)
            & Builders<Session>.Filter.Eq(x => x.OwnerAccountId, ownerId)
            & Builders<Session>.Filter.Ne(x => x.DeviceId, null)
            & Builders<Session>.Filter.Eq(x => x.Status, SessionStatus.Ready)
            & Builders<Session>.Filter.Gt(x => x.ConnectionCredentialExpiresAt, now)
            & Builders<Session>.Filter.Eq(x => x.ConnectionCredentialConsumedAt, null);
        var update = Builders<Session>.Update
            .Set(x => x.Status, SessionStatus.Connecting)
            .Set(x => x.ConnectionCredentialConsumedAt, now)
            .Set(x => x.UpdatedAt, now)
            .Inc(x => x.Version, 1);
        return await Collection.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<Session> { ReturnDocument = ReturnDocument.After }, ct);
    }
}
public sealed class TranscriptRepo(IMongoDbContext db) : MongoRepo<TranscriptEntry>(db), ITranscriptRepo;
public sealed class AgentRepo(IMongoDbContext db) : MongoRepo<Agent>(db), IAgentRepo;
