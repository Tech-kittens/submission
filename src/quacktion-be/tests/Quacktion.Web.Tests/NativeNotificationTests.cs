using System.Net.WebSockets;
using System.Net;
using System.Net.Http.Json;
using Quacktion.Data.Agents;
using Quacktion.Web.Infrastructure;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Audio;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Preparation;
using Xunit;
namespace Quacktion.Web.Tests;

public sealed class NativeNotificationTests
{
    [Theory]
    [InlineData("quack")][InlineData("action_done")][InlineData("needs_confirmation")][InlineData("failed")]
    public void ProtocolUsesVersionPayloadRequestIdAndDocumentedKinds(string kind)
    {
        var frame = Assert.Single(NotificationProtocol.Frames("brief-123", kind, 60, null));
        using var json = JsonDocument.Parse(frame.Data); var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("notify.play", root.GetProperty("type").GetString());
        Assert.Equal("brief-123", root.GetProperty("requestId").GetString());
        Assert.Equal(kind, root.GetProperty("payload").GetProperty("kind").GetString());
        Assert.False(root.GetProperty("payload").GetProperty("audio").GetBoolean());
    }
    [Theory]
    [InlineData(2)][InlineData(4096)][InlineData(4098)][InlineData(320000)]
    public void AudioIsBoundedChunkedAndAlwaysTerminated(int bytes)
    {
        var pcm = Enumerable.Range(0, bytes).Select(x => (byte)x).ToArray();
        var frames = NotificationProtocol.Frames("brief", "quack", 60, pcm);
        using var start = JsonDocument.Parse(frames[1].Data);
        Assert.Equal("notify.audio.start", start.RootElement.GetProperty("type").GetString());
        Assert.Equal(16000, start.RootElement.GetProperty("payload").GetProperty("audio").GetProperty("sampleRate").GetInt32());
        using var end = JsonDocument.Parse(frames[^1].Data);
        Assert.Equal("notify.audio.end", end.RootElement.GetProperty("type").GetString());
        var audio = frames.Where(x => x.Type == WebSocketMessageType.Binary).ToArray();
        Assert.All(audio, x => Assert.InRange(x.Data.Length, 1, 4096));
        Assert.Equal(pcm, audio.SelectMany(x => x.Data).ToArray());
    }
    [Fact]
    public void InvalidIdentifiersAudioAndSpeechAreRejected()
    {
        Assert.Throws<AppException>(() => NotificationProtocol.Frames(new string('x', 49), "quack", 60, null));
        Assert.Throws<AppException>(() => NotificationProtocol.Frames("id", "unknown", 60, null));
        Assert.Throws<AppException>(() => NotificationProtocol.Frames("id", "quack", 0, null));
        Assert.Throws<AppException>(() => NotificationProtocol.Frames("id", "quack", 60, new byte[320002]));
        Assert.Throws<AppException>(() => NotificationProtocol.Frames("id", "quack", 60, new byte[3]));
        Assert.Throws<AppException>(() => NotificationProtocol.Validate(new() { SpokenMessage = "Read this confidential calendar description" }));
    }
    [Theory]
    [InlineData("Yes.", "Accepted")][InlineData("Sure", "Accepted")][InlineData("Go ahead", "Accepted")]
    [InlineData("Tell me!", "Accepted")][InlineData("No", "Dismissed")][InlineData("Not now", "Dismissed")]
    [InlineData("Later", "Postponed")][InlineData("Yes, send that email", null)][InlineData("What time is it?", null)]
    public void OnlyShortRelevantResponsesCanResolveAnOffer(string text, string? expected) => Assert.Equal(expected, BriefingConversation.ShortResponse(text));

    [MongoFact]
    public async Task DurableQueueDeduplicatesAuthorizesAndSurvivesNewServiceInstances()
    {
        await Database(async (db, manager) =>
        {
            var service = Service(db, manager);
            var request = new DeviceNotificationRequest { NotifyId = "brief", OwnerAccountId = "alice" };
            await service.NotifyAsync("duck", request, default);
            await Service(db, manager).NotifyAsync("duck", request, default);
            Assert.Equal(1, await service.Notifications.CountDocumentsAsync(_ => true));
            await service.TickAsync(default);
            Assert.Equal("WaitingForDevice", (await service.Notifications.Find(x => x.Id == "brief").SingleAsync()).Status);
            request.OwnerAccountId = "bob";
            await Assert.ThrowsAsync<AppException>(() => service.NotifyAsync("duck", request, default));
        });
    }
    [MongoFact]
    public async Task QueueAllowsOneInflightAndReconnectRedeliversWithBoundedAttempts()
    {
        await Database(async (db, manager) =>
        {
            var service = Service(db, manager);
            for (var i = 0; i < 5; i++) await service.NotifyAsync("duck", new() { NotifyId = "n" + i, OwnerAccountId = "alice" }, default);
            using var socket = WebSocket.CreateFromStream(new MemoryStream(), true, "quacktion.v1", Timeout.InfiniteTimeSpan);
            using var connection = manager.Register("duck", socket, default);
            var tick = service.TickAsync(default);
            var batch = await connection.Outgoing.Reader.ReadAsync(); Assert.NotNull(batch.Batch); batch.Sent!.SetResult(); await tick;
            await service.TickAsync(default); Assert.False(connection.Outgoing.Reader.TryRead(out _));
            Assert.Equal(1, await service.Notifications.CountDocumentsAsync(x => x.Status == "Sent"));
            manager.Remove("duck", connection);
            using var secondSocket = WebSocket.CreateFromStream(new MemoryStream(), true, "quacktion.v1", Timeout.InfiniteTimeSpan);
            using var second = manager.Register("duck", secondSocket, default);
            tick = Service(db, manager).TickAsync(default);
            var replay = await second.Outgoing.Reader.ReadAsync(); replay.Sent!.SetResult(); await tick;
            Assert.Equal(batch.Batch![0].Data, replay.Batch![0].Data);
        });
    }
    [MongoFact]
    public async Task PlaybackAcknowledgementsDoNotAcceptBriefingAndMuteDoesNotRetry()
    {
        await Database(async (db, manager) =>
        {
            var service = Service(db, manager);
            var device = await db.Collection<Device>().Find(x => x.Id == "duck").SingleAsync();
            foreach (var outcome in new[] { "played", "silent", "expired", "dropped" })
            {
                await service.Notifications.InsertOneAsync(new() { Id = outcome, OwnerAccountId = "alice", DeviceId = "duck", Status = "Sent", ConnectionId = "connection",
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5), Attempts = 1 });
                var ack = JsonSerializer.SerializeToElement(new { version = 1, type = "notify.done", payload = new { notifyId = outcome, outcome, atMs = 123L, atUptimeMs = 456L, clockSource = "sntp" } });
                await service.AcknowledgeAsync(device, "connection", ack, default);
                await service.AcknowledgeAsync(device, "connection", ack, default);
                var n = await service.Notifications.Find(x => x.Id == outcome).SingleAsync();
                Assert.Equal(outcome is "played" or "silent" ? "Acknowledged" : "WaitingForDevice", n.Status);
                Assert.Equal(123L, n.AtMs); Assert.Null(n.OfferExpiresAt); Assert.Null(n.OfferResolution);
            }
        });
    }
    [MongoFact]
    public async Task OffersRequireAudiblePlaybackCurrentConnectionAndExpireOrConsumeOnOtherSpeech()
    {
        await Database(async (db, manager) =>
        {
            var store = new PreparationStore(db, new EmptySearch());
            var p = new MeetingPreparation { Id = "meeting", OwnerAccountId = "alice", Status = "Ready", StartsAt = DateTime.UtcNow.AddMinutes(10), EndsAt = DateTime.UtcNow.AddHours(1),
                Research = new() { KeyTalkingPoints = ["Check the timeout decision"] } };
            await store.InsertAsync(p, default);
            await db.Collection<PreparationPreferences>().InsertOneAsync(new() { Id = "alice", OwnerAccountId = "alice", Enabled = true, MessagingOptIn = true });
            using var socket = WebSocket.CreateFromStream(new MemoryStream(), true, "quacktion.v1", Timeout.InfiniteTimeSpan);
            using var connection = manager.Register("duck", socket, default); connection.AudioActive = true; connection.SessionId = "session";
            var service = Service(db, manager); var conversation = new BriefingConversation(db, store, manager);
            var device = await db.Collection<Device>().Find(x => x.Id == "duck").SingleAsync();
            async Task Offer(string id, string outcome = "played")
            {
                await service.Notifications.InsertOneAsync(new() { Id = id, OwnerAccountId = "alice", DeviceId = "duck", MeetingId = p.Id, CalendarRevision = 1,
                    Status = "Sent", ConnectionId = connection.Id, OfferSessionId = "session", Spoken = true, ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
                await service.AcknowledgeAsync(device, connection.Id, JsonSerializer.SerializeToElement(new { version = 1, payload = new { notifyId = id, outcome } }), default);
            }
            await Offer("silent", "silent"); Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default));
            await store.SavePreferencesAsync("alice", new() { Enabled = true, MessagingOptIn = true }, default);
            Assert.Equal("AcknowledgedSilent", (await store.GetAsync("alice", p.Id, default)).BriefingStatus);
            await Offer("played");
            Assert.Null(await conversation.ObserveAsync("bob", "session", "yes", default));
            connection.PendingBriefingNotificationId = "played";
            Assert.Contains("Check the timeout decision", await conversation.ObserveAsync("alice", "session", "yes", default));
            Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default));
            await Offer("unrelated"); Assert.Null(await conversation.ObserveAsync("alice", "session", "What time is it?", default));
            Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default));
            await Offer("expired");
            await service.Notifications.UpdateOneAsync(x => x.Id == "expired", Builders<DeviceNotification>.Update.Set(x => x.OfferExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
            Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default));
            await Offer("declined"); Assert.Contains("declined", await conversation.ObserveAsync("alice", "session", "later", default));
            await Offer("busy");
            Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default, assistantWasSpeaking: true));
            Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default));
            await Offer("disconnected"); manager.Remove("duck", connection);
            Assert.Null(await conversation.ObserveAsync("alice", "session", "yes", default));
            await db.Collection<Session>().InsertOneAsync(new() { Id = "session", OwnerAccountId = "alice", DeviceId = "duck" });
            Assert.Contains("Check the timeout decision", await conversation.ExecuteAsync("alice", "session", "details", p.Id, default));
            Assert.Contains("Check the timeout decision", await conversation.ExecuteAsync("alice", "session", "read", p.Id, default));
            await conversation.ExecuteAsync("alice", "session", "refresh", p.Id, default);
            Assert.Equal("Pending", (await store.GetAsync("alice", p.Id, default)).Status);
            await conversation.ExecuteAsync("alice", "session", "dismiss", p.Id, default);
            Assert.Equal("Suppressed", (await store.GetAsync("alice", p.Id, default)).BriefingStatus);
        });
    }
    [MongoFact]
    public async Task NativeResearchUsesConfiguredReasoningPersistsEvidenceAndPreservesClarifications()
    {
        await Database(async (db, manager) =>
        {
            var search = new EvidenceSearch(); var store = new PreparationStore(db, search);
            await db.Collection<PreparationPreferences>().InsertOneAsync(new() { Id = "alice", OwnerAccountId = "alice", Enabled = true });
            var p = new MeetingPreparation { OwnerAccountId = "alice", Title = "Security", StartsAt = DateTime.UtcNow.AddHours(1),
                EndsAt = DateTime.UtcNow.AddHours(2), Questions = [new() { Text = "Focus?", Answer = "Timeouts" }] };
            await store.InsertAsync(p, default);
            var handler = new ResearchHttp();
            var reasoning = new GeminiMeetingIntelligence(handler, Options.Create(new GeminiSettings { ApiKey = "test", ReasoningModel = "configured-argon" }), null!, null!, null!, null!);
            var agent = new MeetingPreparationAgent(store, search, reasoning, null!);
            Assert.Equal("Ready", (await agent.PrepareAsync(p.Id, default)).Status);
            var saved = await store.GetAsync("alice", p.Id, default);
            Assert.Equal("alice", search.Owner); Assert.Contains("configured-argon:generateContent", handler.Path);
            Assert.Contains("Timeouts", handler.Body); Assert.Equal("Timeouts", saved.Questions[0].Answer);
            Assert.Contains(saved.Research.Sources, x => x.Id == "memory");
            Assert.Contains(saved.Research.Sources, x => x.Kind == "user" && x.Excerpt == "Timeouts");
            Assert.Equal("Confirm timeout", Assert.Single(saved.Research.OutstandingCommitments));
            Assert.Single(saved.Revisions);
            saved.Status = "Pending"; await store.SaveAsync(saved, default);
            await agent.PrepareAsync(p.Id, default);
            Assert.Equal(2, (await store.GetAsync("alice", p.Id, default)).Revisions.Count);
            handler.InvalidEvidence = true; saved = await store.GetAsync("alice", p.Id, default); saved.Status = "Pending"; await store.SaveAsync(saved, default);
            await Assert.ThrowsAsync<AppException>(() => agent.PrepareAsync(p.Id, default));
        });
    }
    [MongoFact]
    public async Task TaskNotificationsFollowLifecycleAndResolvedApprovalsInvalidateStaleNotifications()
    {
        await Database(async (db, manager) =>
        {
            await db.Collection<Session>().InsertOneAsync(new() { Id = "session", OwnerAccountId = "alice", DeviceId = "duck" });
            var task = new Quacktion.App.Models.Quacktion { Id = "task", OwnerAccountId = "alice", SessionId = "session", Status = QuacktionStatus.PendingApproval };
            await db.Collection<Quacktion.App.Models.Quacktion>().InsertOneAsync(task);
            var service = Service(db, manager);
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            var approval = Assert.Single(await service.Notifications.Find(_ => true).ToListAsync());
            Assert.Equal("needs_confirmation", approval.Kind); Assert.True(await service.RelevantAsync(approval, default));
            await db.Collection<Quacktion.App.Models.Quacktion>().UpdateOneAsync(x => x.Id == task.Id,
                Builders<Quacktion.App.Models.Quacktion>.Update.Set(x => x.Status, QuacktionStatus.Completed));
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            Assert.False(await service.RelevantAsync(approval, default));
            Assert.Single(await service.Notifications.Find(x => x.Kind == "action_done").ToListAsync());
            await db.Collection<Quacktion.App.Models.Quacktion>().InsertOneAsync(new() { Id = "failed", OwnerAccountId = "alice", SessionId = "session", Status = QuacktionStatus.Failed });
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            Assert.Single(await service.Notifications.Find(x => x.Kind == "failed").ToListAsync());
            await db.Collection<Quacktion.App.Models.Quacktion>().InsertOneAsync(new() { Id = "old", OwnerAccountId = "alice", SessionId = "session",
                Status = QuacktionStatus.Completed, CreatedAt = DateTime.UtcNow.AddDays(-1) });
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            Assert.False(await service.Notifications.Find(x => x.QuacktionId == "old").AnyAsync());
            await db.Collection<Quacktion.App.Models.Quacktion>().InsertOneAsync(new() { Id = "other-approval", OwnerAccountId = "alice", SessionId = "session", Status = QuacktionStatus.PendingApproval });
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            var done = await service.Notifications.Find(x => x.Kind == "action_done").SingleAsync();
            await service.Notifications.UpdateOneAsync(x => x.Id == done.Id, Builders<DeviceNotification>.Update.Set(x => x.Status, "Sent").Set(x => x.ConnectionId, "connection"));
            var device = await db.Collection<Device>().Find(x => x.Id == "duck").SingleAsync();
            await service.AcknowledgeAsync(device, "connection", JsonSerializer.SerializeToElement(new { version = 1, payload = new { notifyId = done.Id, outcome = "played" } }), default);
            await DeviceNotificationWorker.LifecycleAsync(db, service, default);
            Assert.Equal(2, await service.Notifications.CountDocumentsAsync(x => x.QuacktionId == "other-approval"));
        });
    }
    [MongoFact]
    public async Task CancelledRescheduledAndEndedMeetingsCannotDeliverStaleBriefings()
    {
        await Database(async (db, manager) =>
        {
            var p = new MeetingPreparation { Id = "meeting", OwnerAccountId = "alice", Status = "Ready", StartsAt = DateTime.UtcNow.AddMinutes(10), EndsAt = DateTime.UtcNow.AddHours(1) };
            await db.Collection<MeetingPreparation>().InsertOneAsync(p);
            await db.Collection<PreparationPreferences>().InsertOneAsync(new() { Id = "alice", OwnerAccountId = "alice", Enabled = true, MessagingOptIn = true });
            var n = new DeviceNotification { OwnerAccountId = "alice", DeviceId = "duck", MeetingId = p.Id, CalendarRevision = 1, ExpiresAt = p.EndsAt };
            var service = Service(db, manager); Assert.True(await service.RelevantAsync(n, default));
            await db.Collection<MeetingPreparation>().UpdateOneAsync(x => x.Id == p.Id, Builders<MeetingPreparation>.Update.Set(x => x.CalendarRevision, 2));
            Assert.False(await service.RelevantAsync(n, default)); n.CalendarRevision = 2;
            await db.Collection<MeetingPreparation>().UpdateOneAsync(x => x.Id == p.Id, Builders<MeetingPreparation>.Update.Set(x => x.Status, "Cancelled"));
            Assert.False(await service.RelevantAsync(n, default));
            await db.Collection<MeetingPreparation>().UpdateOneAsync(x => x.Id == p.Id, Builders<MeetingPreparation>.Update.Set(x => x.Status, "Ready").Set(x => x.EndsAt, DateTime.UtcNow.AddMinutes(-1)));
            Assert.False(await service.RelevantAsync(n, default));
        });
    }
    private sealed class EvidenceSearch : IMeetingMemorySearchService
    {
        public string? Owner;
        public Task<MeetingMemorySearchResult> SearchAsync(string owner, MeetingMemorySearchRequest request, CancellationToken ct)
        { Owner = owner; return Task.FromResult(new MeetingMemorySearchResult([new("memory", "previous", "Security", DateTime.UtcNow.AddDays(-1), "commitment", "Confirm timeout", [], [], 1)], "rrf")); }
    }
    private sealed class ResearchHttp : HttpMessageHandler, IHttpClientFactory
    {
        public string? Path; public string? Body; public bool InvalidEvidence;
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://example.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath; Body = await request.Content!.ReadAsStringAsync(ct);
            var research = new PreparationResearch { Summary = "Review security", KeyTalkingPoints = ["Confirm timeout"],
                Commitments = [new("Confirm timeout", [InvalidEvidence ? "invented" : "memory"])] };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = JsonSerializer.Serialize(research) } } } } } }) };
        }
    }
    private sealed class EmptySearch : IMeetingMemorySearchService
    { public Task<MeetingMemorySearchResult> SearchAsync(string owner, MeetingMemorySearchRequest request, CancellationToken ct) => Task.FromResult(new MeetingMemorySearchResult([], "rrf")); }
    private static DeviceNotificationService Service(IMongoDbContext db, DeviceConnectionManager manager) => new(db, manager, null!, NullLogger<DeviceNotificationService>.Instance);
    private static async Task Database(Func<MongoDbContext, DeviceConnectionManager, Task> action)
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO")!; var name = "quacktion_native_test_" + Guid.NewGuid().ToString("N");
        var db = new MongoDbContext(Options.Create(new MongoDbSettings { ConnectionString = uri, Database = name }));
        try
        {
            await db.InitializeAsync(default);
            await db.Collection<Account>().InsertOneAsync(new() { Id = "alice", Email = "alice@example.test", IsActive = true });
            await db.Collection<Device>().InsertOneAsync(new() { Id = "duck", OwnerAccountId = "alice", CredentialHash = "test" });
            await action(db, new DeviceConnectionManager());
        }
        finally { await new MongoClient(uri).DropDatabaseAsync(name); }
    }
}
