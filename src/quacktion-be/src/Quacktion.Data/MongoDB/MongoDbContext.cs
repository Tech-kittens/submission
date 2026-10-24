using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Quacktion.App.Models;
namespace Quacktion.Data.MongoDB;

public sealed class MongoDbSettings
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string Database { get; set; } = "quacktion";
}
public interface IMongoDbContext { IMongoCollection<T> Collection<T>() where T : Entity; Task InitializeAsync(CancellationToken ct); }
public sealed class MongoDbContext : IMongoDbContext
{
    private readonly IMongoDatabase database;
    static MongoDbContext()
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(Account)))
            BsonClassMap.RegisterClassMap<Account>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapMember(account => account.Name).SetElementName("DisplayName");
            });
    }

    public MongoDbContext(IOptions<MongoDbSettings> options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        database = new MongoClient(settings).GetDatabase(options.Value.Database);
    }
    public IMongoCollection<T> Collection<T>() where T : Entity => database.GetCollection<T>(typeof(T).Name switch
    {
        nameof(Account) => "accounts",
        nameof(RefreshToken) => "refresh_tokens",
        nameof(Device) => "devices",
        nameof(PairingChallenge) => "device_pairing_challenges",
        nameof(Session) => "sessions",
        nameof(TranscriptEntry) => "transcript_entries",
        "Quacktion" => "quacktions",
        nameof(QuacktionExecution) => "quacktion_executions",
        nameof(ApprovalRequest) => "approval_requests",
        nameof(Agent) => "agents",
        _ => throw new InvalidOperationException("Unknown collection")
    });
    public async Task InitializeAsync(CancellationToken ct)
    {
        await Index<Account>(Builders<Account>.IndexKeys.Ascending(x => x.Email), new() { Unique = true }, ct);
        await Index<Device>(Builders<Device>.IndexKeys.Ascending(x => x.DeviceIdentifier), new() { Unique = true }, ct);
        await Collection<Device>().Indexes.CreateOneAsync(new CreateIndexModel<Device>(
            Builders<Device>.IndexKeys.Ascending(x => x.CredentialHash),
            new CreateIndexOptions<Device> { Unique = true, PartialFilterExpression = new BsonDocument("CredentialHash", new BsonDocument("$type", "string")) }), cancellationToken: ct);
        await Index<Device>(Builders<Device>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<RefreshToken>(Builders<RefreshToken>.IndexKeys.Ascending(x => x.ExpiresAt), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<PairingChallenge>(Builders<PairingChallenge>.IndexKeys.Ascending(x => x.ExpiresAt), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<Session>(Builders<Session>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Collection<Session>().Indexes.CreateOneAsync(new CreateIndexModel<Session>(
            Builders<Session>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.DeviceId).Ascending(x => x.StartRequestId),
            new CreateIndexOptions<Session> { Unique = true, PartialFilterExpression = new BsonDocument("StartRequestId", new BsonDocument("$type", "string")) }), cancellationToken: ct);
        await Index<TranscriptEntry>(Builders<TranscriptEntry>.IndexKeys.Ascending(x => x.SessionId).Ascending(x => x.Source).Ascending(x => x.SourceEventId), new() { Unique = true }, ct);
        await Index<TranscriptEntry>(Builders<TranscriptEntry>.IndexKeys.Ascending(x => x.SessionId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<App.Models.Quacktion>(Builders<App.Models.Quacktion>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.NextAttemptAt).Ascending(x => x.LeaseExpiresAt), new(), ct);
        await Index<App.Models.Quacktion>(Builders<App.Models.Quacktion>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.SessionId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<App.Models.Quacktion>(Builders<App.Models.Quacktion>.IndexKeys.Ascending(x => x.IdempotencyKey), new() { Unique = true }, ct);
        await Index<QuacktionExecution>(Builders<QuacktionExecution>.IndexKeys.Ascending(x => x.QuacktionId).Ascending(x => x.Attempt), new() { Unique = true }, ct);
        await Index<ApprovalRequest>(Builders<ApprovalRequest>.IndexKeys.Ascending(x => x.QuacktionId), new() { Unique = true }, ct);
    }
    private Task Index<T>(IndexKeysDefinition<T> keys, CreateIndexOptions options, CancellationToken ct) where T : Entity => Collection<T>().Indexes.CreateOneAsync(new CreateIndexModel<T>(keys, options), cancellationToken: ct);
}
