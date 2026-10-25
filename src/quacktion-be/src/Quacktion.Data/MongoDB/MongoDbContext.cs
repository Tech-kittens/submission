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
        if (!BsonClassMap.IsClassMapRegistered(typeof(Session)))
            BsonClassMap.RegisterClassMap<Session>(classMap =>
            {
                classMap.AutoMap();
                // Older session documents contain the now-removed credential hash field.
                classMap.SetIgnoreExtraElements(true);
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
        nameof(DeviceNotification) => "device_notifications",
        nameof(MeetingMemoryDocument) => "meeting_memory",
        nameof(MeetingPreparation) => "meeting_preparations",
        nameof(PreparationPreferences) => "meeting_preparation_preferences",
        nameof(CalendarSyncState) => "calendar_sync_states",
        nameof(Account) => "accounts",
        nameof(RefreshToken) => "refresh_tokens",
        nameof(Device) => "devices",
        nameof(PairingChallenge) => "device_pairing_challenges",
        nameof(Session) => "sessions",
        nameof(DeviceTransportState) => "device_transport_states",
        nameof(ConversationState) => "conversation_sessions",
        nameof(ConversationEvent) => "conversation_events",
        nameof(Meeting) => "meetings",
        nameof(MeetingAnalysis) => "meeting_analyses",
        nameof(MeetingInsight) => "meeting_insights",
        nameof(MeetingTranscriptTurn) => "meeting_transcript_turns",
        nameof(MeetingSegment) => "meeting_segments",
        nameof(MeetingEvent) => "meeting_events",
        nameof(TranscriptEntry) => "transcript_entries",
        "Quacktion" => "quacktions",
        nameof(QuacktionExecution) => "quacktion_executions",
        nameof(ApprovalRequest) => "approval_requests",
        nameof(Agent) => "agents",
        nameof(RealtimeState) => "realtime_states",
        nameof(MeetingToolCall) => "meeting_tool_calls",
        nameof(UserMcpConnection) => "user_mcp_connections",
        nameof(McpCredential) => "mcp_credentials",
        nameof(McpOAuthTransaction) => "mcp_oauth_transactions",
        nameof(McpExecutionAudit) => "mcp_execution_audit",
        nameof(McpSkillExecution) => "mcp_skill_executions",
        nameof(McpServerRegistration) => "mcp_servers",
        _ => throw new InvalidOperationException("Unknown collection")
    });
    public async Task InitializeAsync(CancellationToken ct)
    {
        await Index<DeviceNotification>(Builders<DeviceNotification>.IndexKeys.Ascending(x => x.DeviceId).Ascending(x => x.Status).Ascending(x => x.NextAttemptAt), new(), ct);
        await Index<DeviceNotification>(Builders<DeviceNotification>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.ExpiresAt), new(), ct);
        await Index<DeviceNotification>(Builders<DeviceNotification>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.CreatedAt), new(), ct);
        await Index<DeviceNotification>(Builders<DeviceNotification>.IndexKeys.Ascending(x => x.RetainUntil), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<MeetingPreparation>(Builders<MeetingPreparation>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.ResearchNextAttemptAt).Ascending(x => x.ResearchLeaseUntil), new(), ct);
        await Index<ConversationEvent>(Builders<ConversationEvent>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.ConversationId).Ascending(x => x.Sequence), new() { Unique = true }, ct);
        await Index<ConversationState>(Builders<ConversationState>.IndexKeys.Ascending(x => x.LeaseExpiresAt), new(), ct);
        // Old plaintext checkpoints cannot be trusted after migration; restore context instead.
        await Collection<RealtimeState>().UpdateManyAsync(Builders<RealtimeState>.Filter.Ne(x => x.ResumptionHandle, null), Builders<RealtimeState>.Update.Unset(x => x.ResumptionHandle), cancellationToken: ct);
        await Index<MeetingMemoryDocument>(Builders<MeetingMemoryDocument>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.MeetingId).Ascending(x => x.Active), new(), ct);
        await Index<MeetingPreparation>(Builders<MeetingPreparation>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.GoogleAccountId).Ascending(x => x.CalendarId).Ascending(x => x.CalendarEventId), new() { Unique = true }, ct);
        await Index<MeetingPreparation>(Builders<MeetingPreparation>.IndexKeys.Ascending(x => x.BriefingStatus).Ascending(x => x.BriefingDueAt).Ascending(x => x.LeaseUntil), new(), ct);
        await Index<MeetingPreparation>(Builders<MeetingPreparation>.IndexKeys.Ascending(x => x.ExpiresAt), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<PreparationPreferences>(Builders<PreparationPreferences>.IndexKeys.Ascending(x => x.OwnerAccountId), new() { Unique = true }, ct);
        await Index<CalendarSyncState>(Builders<CalendarSyncState>.IndexKeys.Ascending(x => x.NextAttemptAt).Ascending(x => x.LeaseUntil), new(), ct);
        await Index<MeetingToolCall>(Builders<MeetingToolCall>.IndexKeys.Ascending(x => x.SessionId).Ascending(x => x.Delivered).Ascending(x => x.CreatedAt), new(), ct);
        await Index<McpSkillExecution>(Builders<McpSkillExecution>.IndexKeys.Ascending(x => x.UserId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<McpSkillExecution>(Builders<McpSkillExecution>.IndexKeys.Ascending(x => x.ParentTaskId).Ascending(x => x.SkillId), new(), ct);
        await Index<RealtimeState>(Builders<RealtimeState>.IndexKeys.Ascending(x => x.SessionId), new() { Unique = true }, ct);
        await Index<Account>(Builders<Account>.IndexKeys.Ascending(x => x.Email), new() { Unique = true }, ct);
        await Index<Device>(Builders<Device>.IndexKeys.Ascending(x => x.DeviceIdentifier), new() { Unique = true }, ct);
        await Collection<Device>().Indexes.CreateOneAsync(new CreateIndexModel<Device>(
            Builders<Device>.IndexKeys.Ascending(x => x.CredentialHash),
            new CreateIndexOptions<Device> { Unique = true, PartialFilterExpression = new BsonDocument("CredentialHash", new BsonDocument("$type", "string")) }), cancellationToken: ct);
        await Index<Device>(Builders<Device>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<RefreshToken>(Builders<RefreshToken>.IndexKeys.Ascending(x => x.ExpiresAt), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<PairingChallenge>(Builders<PairingChallenge>.IndexKeys.Ascending(x => x.ExpiresAt), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<Session>(Builders<Session>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<Meeting>(Builders<Meeting>.IndexKeys.Ascending(x => x.OwnerAccountId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
        await Index<Meeting>(Builders<Meeting>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.EndedAt), new(), ct);
        await Index<MeetingAnalysis>(Builders<MeetingAnalysis>.IndexKeys.Ascending(x => x.MeetingId), new() { Unique = true }, ct);
        await Index<MeetingInsight>(Builders<MeetingInsight>.IndexKeys.Ascending(x => x.MeetingId).Ascending(x => x.LastSegmentId), new() { Unique = true }, ct);
        await Index<MeetingTranscriptTurn>(Builders<MeetingTranscriptTurn>.IndexKeys.Ascending(x => x.MeetingId).Ascending(x => x.ChunkIndex).Ascending(x => x.StartMs).Ascending(x => x.Id), new(), ct);
        await Index<MeetingSegment>(Builders<MeetingSegment>.IndexKeys.Ascending(x => x.DeviceId).Ascending(x => x.SegmentId), new() { Unique = true }, ct);
        await Index<MeetingSegment>(Builders<MeetingSegment>.IndexKeys.Ascending(x => x.MeetingId).Ascending(x => x.BootId).Ascending(x => x.StartUptimeMs).Ascending(x => x.SegmentId), new(), ct);
        await Index<MeetingEvent>(Builders<MeetingEvent>.IndexKeys.Ascending(x => x.MeetingId).Ascending(x => x.CreatedAt).Ascending(x => x.Id), new(), ct);
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
        await Index<App.Models.Quacktion>(Builders<App.Models.Quacktion>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.Approval!.ExpiresAt), new(), ct);
        await Index<McpServerRegistration>(Builders<McpServerRegistration>.IndexKeys.Ascending(x => x.Provider).Ascending(x => x.Service), new() { Unique = true }, ct);
        await Index<UserMcpConnection>(Builders<UserMcpConnection>.IndexKeys.Ascending(x => x.UserId).Ascending(x => x.Provider).Ascending(x => x.ProviderAccountId).Ascending(x => x.ServerId), new() { Unique = true }, ct);
        await Index<UserMcpConnection>(Builders<UserMcpConnection>.IndexKeys.Ascending(x => x.UserId).Ascending(x => x.CreatedAt), new(), ct);
        await Index<McpCredential>(Builders<McpCredential>.IndexKeys.Ascending(x => x.ConnectionId), new() { Unique = true }, ct);
        await Index<McpOAuthTransaction>(Builders<McpOAuthTransaction>.IndexKeys.Ascending(x => x.ExpiresAt), new() { ExpireAfter = TimeSpan.Zero }, ct);
        await Index<McpExecutionAudit>(Builders<McpExecutionAudit>.IndexKeys.Ascending(x => x.UserId).Ascending(x => x.CreatedAt), new(), ct);
    }
    private Task Index<T>(IndexKeysDefinition<T> keys, CreateIndexOptions options, CancellationToken ct) where T : Entity => Collection<T>().Indexes.CreateOneAsync(new CreateIndexModel<T>(keys, options), cancellationToken: ct);
}
