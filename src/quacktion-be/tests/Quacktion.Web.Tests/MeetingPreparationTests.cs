using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Preparation;
using Quacktion.Data.Repositories;
using Xunit;
namespace Quacktion.Web.Tests;

public sealed class MeetingPreparationTests
{
    [Theory]
    [InlineData("confirmed", "accepted", true)]
    [InlineData("tentative", "needsAction", true)]
    [InlineData("cancelled", "accepted", false)]
    [InlineData("confirmed", "declined", false)]
    public void CalendarInvitationsAndCancellations(string status, string response, bool relevant)
    {
        var e = CalendarMeetingParser.Parse(JsonSerializer.SerializeToElement(new { id = "instance_20261101", recurringEventId = "series",
            summary = "Security review", status, start = new { dateTime = "2026-11-01T14:00:00+00:00", timeZone = "Europe/London" },
            end = new { dateTime = "2026-11-01T15:00:00+00:00" }, attendees = new[] {
                new { email = "user@example.com", self = true, responseStatus = response },
                new { email = "sarah@example.com", self = false, responseStatus = "accepted" } } }));
        Assert.Equal(relevant, CalendarMeetingParser.Relevant(e, new() { Enabled = true }));
        Assert.Equal("series", e.RecurringEventId);
        Assert.False(CalendarMeetingParser.Relevant(e, new() { Enabled = true, ExcludedEventIds = [e.Id] }));
    }
    [Fact]
    public void CalendarIdentityIncludesOwnerAccountCalendarAndInstance()
    {
        var id = CalendarMeetingParser.Identity("owner", "account", "calendar", "event");
        Assert.Equal(id, CalendarMeetingParser.Identity("owner", "account", "calendar", "event"));
        Assert.NotEqual(id, CalendarMeetingParser.Identity("owner", "another", "calendar", "event"));
        Assert.NotEqual(id, CalendarMeetingParser.Identity("owner", "account", "other", "event"));
        Assert.NotEqual(id, CalendarMeetingParser.Identity("owner", "account", "calendar", "other-instance"));
    }
    [Fact]
    public void PersonalAndAllDayEventsAreExcluded()
    {
        var e = Event();
        Assert.False(CalendarMeetingParser.Relevant(e with { Attendees = [], Description = "" }, new() { Enabled = true }));
        Assert.False(CalendarMeetingParser.Relevant(e with { AllDay = true }, new() { Enabled = true }));
    }
    [Fact]
    public void BothAtlasBranchesFilterOwnersAndParticipantsBeforeRanking()
    {
        var settings = new PreparationSettings();
        var (text, vector) = MeetingMemoryPipeline.Branches("alice", new() { Query = "OAuth", ParticipantEmails = ["SARAH@EXAMPLE.COM"], DocumentTypes = ["decision"], From = DateTime.UtcNow.AddDays(-30) }, [1, 0], settings);
        Assert.True(text[0].Contains("$search")); Assert.True(vector[0].Contains("$vectorSearch"));
        var filter = vector[0]["$vectorSearch"]["filter"].AsBsonDocument;
        Assert.Equal("alice", filter["OwnerAccountId"]); Assert.True(filter["Active"].AsBoolean);
        Assert.Equal("sarah@example.com", filter["ParticipantEmails"]["$in"][0].AsString);
        var lexical = text[0]["$search"]["compound"]["filter"].ToJson();
        Assert.Contains("alice", lexical); Assert.Contains("sarah@example.com", lexical); Assert.Contains("MeetingDate", lexical);
        Assert.DoesNotContain("EmbeddingModel", lexical); // Missing embeddings still participate in lexical search.
        Assert.Contains("$rankFusion", MeetingMemoryPipeline.Native(text, vector, 10)[0].ToJson());
    }
    [Fact]
    public void ReciprocalRankFusionCombinesExactAndSemanticResultsWithoutConfidenceClaims()
    {
        BsonDocument D(string id) => new("_id", id);
        var hits = MeetingMemoryPipeline.Fuse([D("exact"), D("both")], [D("semantic"), D("both")], 10);
        Assert.Equal("both", hits[0].Document["_id"].AsString);
        Assert.Equal(2d / 62, hits[0].Score, 10);
        Assert.Equal(3, hits.Count);
        Assert.Empty(MeetingMemoryPipeline.Fuse([], [], 10));
    }
    [Fact]
    public void BriefingsRespectOptInCancellationLateBookingAndStartTime()
    {
        var now = DateTime.UtcNow; var p = Preparation(); var prefs = new PreparationPreferences { Enabled = true, MessagingOptIn = true };
        p.StartsAt = now.AddMinutes(5); p.BriefingDueAt = now.AddMinutes(-25);
        Assert.True(BriefingPolicy.Eligible(p, prefs, now));
        prefs.MessagingOptIn = false; Assert.False(BriefingPolicy.Eligible(p, prefs, now)); prefs.MessagingOptIn = true;
        p.Status = "Cancelled"; Assert.False(BriefingPolicy.Eligible(p, prefs, now)); p.Status = "Pending";
        Assert.False(BriefingPolicy.Eligible(p, prefs, now.AddMinutes(6)));
        Assert.True(BriefingPolicy.RetryAt(now, 3) > BriefingPolicy.RetryAt(now, 1));
    }
    [Fact]
    public void TranscriptChunksAreBoundedAndPreserveText()
    {
        var text = string.Join(' ', Enumerable.Repeat("authentication deployment context", 400));
        var chunks = MeetingMemoryIngestion.Chunk(text).ToArray();
        Assert.True(chunks.Length > 1); Assert.All(chunks, c => Assert.InRange(c.Length, 1, 2400));
        Assert.Equal(text, string.Join(' ', chunks));
    }
    [MongoFact]
    public async Task CalendarDedupRescheduleCancellationAndResearchConcurrency()
    {
        await WithDatabase(async db =>
        {
            var store = Store(db); var sync = new CalendarPreparationSync(db, store, null!, Options.Create(new PreparationSettings()), NullLogger<CalendarPreparationSync>.Instance);
            var state = new CalendarSyncState { OwnerAccountId = "alice", GoogleAccountId = "google-account", CalendarId = "primary", ConnectionId = "connection" };
            var prefs = new PreparationPreferences { Enabled = true, MessagingOptIn = true }; var e = Event();
            await sync.ApplyAsync(state, e, prefs, default); await sync.ApplyAsync(state, e, prefs, default);
            var p = Assert.Single(await store.Preparations.Find(_ => true).ToListAsync());
            Assert.Single(p.Outbox); Assert.Equal("meeting.created", p.Outbox[0].Type);
            await Assert.ThrowsAsync<AppException>(() => store.GetAsync("bob", p.Id, default));
            await store.UpdateResearchAsync("alice", p.Id, p.Version, p.CalendarRevision, "research-1", new() { Summary = "OAuth context" }, true, default);
            await store.UpdateResearchAsync("alice", p.Id, p.Version, p.CalendarRevision, "research-1", new() { Summary = "OAuth context" }, true, default);
            await Assert.ThrowsAsync<AppException>(() => store.UpdateResearchAsync("alice", p.Id, p.Version, p.CalendarRevision, "research-2", new(), true, default));
            await sync.ApplyAsync(state, e with { StartsAt = e.StartsAt.AddHours(2), EndsAt = e.EndsAt.AddHours(2) }, prefs, default);
            p = await store.GetAsync("alice", p.Id, default);
            Assert.Equal(2, p.CalendarRevision); Assert.Equal(new DateTimeOffset(e.StartsAt.AddHours(2).AddMinutes(-30)).ToUnixTimeMilliseconds(), new DateTimeOffset(p.BriefingDueAt).ToUnixTimeMilliseconds());
            await sync.ApplyAsync(state, e with { Cancelled = true }, prefs, default);
            p = await store.GetAsync("alice", p.Id, default);
            Assert.Equal("Cancelled", p.Status); Assert.Equal("Cancelled", p.BriefingStatus);
            Assert.Single(p.Outbox, x => x.Status == "Pending"); Assert.Equal("meeting.cancelled", p.Outbox.Last().Type);
        });
    }
    [MongoFact]
    public async Task ClarificationsAreCorrelatedBoundedIdempotentAndLateRepliesDoNotRestartResearch()
    {
        await WithDatabase(async db =>
        {
            var store = Store(db); var p = Preparation(); await store.InsertAsync(p, default);
            await store.SavePreferencesAsync("alice", new() { Enabled = true, MessagingOptIn = true, ClarificationsEnabled = true }, default);
            var q = await store.QuestionAsync("alice", p.Id, "one", "Has OAuth been fixed?", default);
            Assert.Equal(q.Id, (await store.QuestionAsync("alice", p.Id, "one", q.Text, default)).Id);
            await Assert.ThrowsAsync<AppException>(() => store.QuestionAsync("alice", p.Id, "two", "Follow up?", default));
            await Assert.ThrowsAsync<AppException>(() => store.AnswerAsync("bob", p.Id, q.Id, "yes", default));
            await Assert.ThrowsAsync<AppException>(() => store.AnswerAsync("alice", p.Id, "wrong", "yes", default));
            p = await store.GetAsync("alice", p.Id, default); p.StartsAt = DateTime.UtcNow.AddMinutes(-1); await store.SaveAsync(p, default);
            await store.AnswerAsync("alice", p.Id, q.Id, "Discuss session timeouts", default);
            p = await store.GetAsync("alice", p.Id, default);
            Assert.True(p.Questions[0].LateReply); Assert.DoesNotContain(p.Outbox, x => x.Type == "meeting.preparation_requested");
        });
    }
    [MongoFact]
    public async Task DeliveryClaimsAreExclusiveAndFailuresRetryWithDeadLetters()
    {
        await WithDatabase(async db =>
        {
            var store = Store(db); var p = Preparation(); PreparationStore.Event(p, "meeting.created"); await store.InsertAsync(p, default);
            await store.SavePreferencesAsync("alice", new() { Enabled = true, MessagingOptIn = true }, default);
            var delivery = new FakeDelivery();
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => new PreparationDeliveryWorker(Store(db), delivery, NullLogger<PreparationDeliveryWorker>.Instance).TickAsync(default)));
            Assert.Equal(1, delivery.Calls); p = await store.GetAsync("alice", p.Id, default); Assert.Equal("Delivered", p.Outbox[0].Status);
            PreparationStore.Event(p, "meeting.updated"); p.Outbox.Last().Attempts = 9; await store.SaveAsync(p, default);
            delivery.Fail = true;
            await new PreparationDeliveryWorker(store, delivery, NullLogger<PreparationDeliveryWorker>.Instance).TickAsync(default);
            p = await store.GetAsync("alice", p.Id, default); Assert.Equal("DeadLetter", p.Outbox.Last().Status);
            Assert.True(p.Outbox.Last().NextAttemptAt > DateTime.UtcNow);
        });
    }
    [MongoFact]
    public async Task BackfillChunksDeduplicatesReprocessesAndDeletesMemory()
    {
        await WithDatabase(async db =>
        {
            var meeting = new Meeting { OwnerAccountId = "alice", Name = "Security", Status = MeetingStatus.Completed,
                ProcessedAt = DateTime.UtcNow, FinalTranscriptStatus = "Ready", ParticipantEmails = ["sarah@example.com"] };
            await db.Collection<Meeting>().InsertOneAsync(meeting);
            var finding = new MeetingFinding { Text = "Review token expiry", Status = "confirmed", EvidenceSegmentIds = ["segment"] };
            await db.Collection<MeetingAnalysis>().InsertOneAsync(new() { MeetingId = meeting.Id, OwnerAccountId = "alice", Summary = "Security review", Decisions = [finding, finding] });
            await db.Collection<MeetingTranscriptTurn>().InsertOneAsync(new() { Id = "turn", MeetingId = meeting.Id, OwnerAccountId = "alice", Text = new string('a', 5000) });
            var embedding = new FakeEmbedding();
            var ingestion = new MeetingMemoryIngestion(db, new MeetingRepo(db), new MeetingAnalysisRepo(db), new MeetingTranscriptTurnRepo(db),
                new MeetingSegmentRepo(db), embedding, Options.Create(new PreparationSettings { MemoryEnabled = true }));
            await ingestion.TickAsync(default); await ingestion.TickAsync(default);
            var memory = await db.Collection<MeetingMemoryDocument>().Find(x => x.OwnerAccountId == "alice").ToListAsync();
            Assert.Equal(5, memory.Count); Assert.Single(memory, x => x.Type == "decision");
            Assert.All(memory, x => Assert.True(x.Active)); Assert.Equal(5, embedding.Calls);
            Assert.All(memory.Where(x => x.Type == "transcript"), x => Assert.Contains("turn", x.SourceSegmentIds));
            await Assert.ThrowsAsync<AppException>(() => ingestion.IndexAsync("bob", meeting.Id, default));
            await db.Collection<MeetingTranscriptTurn>().UpdateOneAsync(x => x.Id == "turn",
                Builders<MeetingTranscriptTurn>.Update.Set(x => x.Text, "Corrected final transcript"));
            await db.Collection<Meeting>().UpdateOneAsync(x => x.Id == meeting.Id,
                Builders<Meeting>.Update.Set(x => x.MemoryIndexedAt, null).Set(x => x.MemoryGeneration, null));
            await ingestion.TickAsync(default);
            var reindexed = await db.Collection<MeetingMemoryDocument>().Find(_ => true).ToListAsync();
            Assert.Equal(3, reindexed.Count);
            Assert.Equal("Corrected final transcript", Assert.Single(reindexed, x => x.Type == "transcript").Content);
            await ingestion.DeleteAsync("bob", meeting.Id, default);
            Assert.Equal(3, await db.Collection<MeetingMemoryDocument>().CountDocumentsAsync(_ => true));
            await ingestion.DeleteAsync("alice", meeting.Id, default);
            Assert.Equal(0, await db.Collection<MeetingMemoryDocument>().CountDocumentsAsync(_ => true));
        });
    }
    [MongoFact]
    public async Task DueBriefingQueuesOnceAndCancellationSuppressesDelivery()
    {
        await WithDatabase(async db =>
        {
            var store = Store(db); var p = Preparation(); p.BriefingDueAt = DateTime.UtcNow.AddMinutes(-1);
            await store.InsertAsync(p, default);
            await store.SavePreferencesAsync("alice", new() { Enabled = true, MessagingOptIn = true }, default);
            p = await store.GetAsync("alice", p.Id, default); p.BriefingDueAt = DateTime.UtcNow.AddMinutes(-1); await store.SaveAsync(p, default);
            var delivery = new FakeDelivery(); var worker = new PreparationDeliveryWorker(store, delivery, NullLogger<PreparationDeliveryWorker>.Instance);
            await worker.TickAsync(default); await worker.TickAsync(default);
            Assert.Equal(1, delivery.Calls);
            p = await store.GetAsync("alice", p.Id, default); Assert.Equal("Queued", p.BriefingStatus);
            // Scheduling does not imply playback acknowledgement.
            Assert.Single(p.Outbox, x => x.Type == "meeting.briefing_requested");
            p.Status = "Cancelled"; p.BriefingStatus = "Pending"; await store.SaveAsync(p, default);
            await worker.TickAsync(default); Assert.Equal(1, delivery.Calls);
        });
    }
    private sealed class FakeEmbedding : IMeetingEmbeddingService
    {
        public int Calls; public string Model => "test-model"; public int Dimensions => 3;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct, bool query = false)
        { Calls++; return Task.FromResult(new float[] { 1, 0, 0 }); }
    }
    private static CalendarMeeting Event() => new("event", "series", "Security review", "Review OAuth", DateTime.UtcNow.AddHours(3), DateTime.UtcNow.AddHours(4), "Europe/London", [new("alice@example.com", "Alice"), new("sarah@example.com", "Sarah")], false, false, false);
    private static MeetingPreparation Preparation() => new() { OwnerAccountId = "alice", GoogleAccountId = "account", CalendarId = "primary", CalendarEventId = Guid.NewGuid().ToString("N"), StartsAt = DateTime.UtcNow.AddHours(3), EndsAt = DateTime.UtcNow.AddHours(4), BriefingDueAt = DateTime.UtcNow.AddHours(2.5), ExpiresAt = DateTime.UtcNow.AddDays(30) };
    private static PreparationStore Store(IMongoDbContext db) => new(db, new FakeSearch());
    private sealed class FakeSearch : IMeetingMemorySearchService
    { public Task<MeetingMemorySearchResult> SearchAsync(string authenticatedOwner, MeetingMemorySearchRequest request, CancellationToken ct) => Task.FromResult(new MeetingMemorySearchResult([], "reciprocal_rank_fusion")); }
    private sealed class FakeDelivery : IPreparationDelivery
    {
        public int Calls; public bool Fail;
        public async Task DeliverAsync(MeetingPreparation preparation, PreparationEvent message, CancellationToken ct)
        { Interlocked.Increment(ref Calls); await Task.Delay(50, ct); if (Fail) throw new HttpRequestException(); }
    }
    private static async Task WithDatabase(Func<MongoDbContext, Task> test)
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO")!; var name = "quacktion_preparation_test_" + Guid.NewGuid().ToString("N");
        var db = new MongoDbContext(Options.Create(new MongoDbSettings { ConnectionString = uri, Database = name }));
        try
        {
            await db.InitializeAsync(default);
            await db.Collection<Account>().InsertOneAsync(new() { Id = "alice", Email = "alice@example.test", IsActive = true });
            await test(db);
        }
        finally { await new MongoClient(uri).DropDatabaseAsync(name); }
    }
}
