using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.App.Tests;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Repositories;
using Quacktion.Data.Sessions;
using Xunit;
namespace Quacktion.Web.Tests;

public sealed class SessionPersistenceTests
{
    private static HandleCipher Cipher() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["SessionPersistence:HandleEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build());
    [Fact]
    public void HandlesAreRandomizedAuthenticatedBoundToOwnerAndNeverSerialized()
    {
        var cipher = Cipher(); var encrypted = cipher.Encrypt("secret", "a", "session");
        Assert.NotEqual(encrypted, cipher.Encrypt("secret", "a", "session"));
        Assert.Equal("secret", cipher.Decrypt(encrypted, "a", "session"));
        Assert.Null(cipher.Decrypt(encrypted, "b", "session"));
        Assert.Null(cipher.Decrypt(encrypted, "a", "another"));
        Assert.Null(cipher.Decrypt("corrupt", "a", "session"));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(new RealtimeState { ResumptionHandle = "secret", EncryptedResumptionHandle = "secret" }));
        Assert.Null(new HandleCipher(new ConfigurationBuilder().Build()).Encrypt("secret", "a", "s"));
    }
    [Fact]
    public async Task SuspensionPreservesLogicalIdentityHistoryTasksAndApprovalAndExplicitEndExcludesAutomaticResume()
    {
        var f = new Fixture(); var (owner, original) = await f.Seed();
        var session = (await f.Sessions.GetAsync(original.Id, default))!;
        await f.Transcripts.InsertAsync(new() { SessionId = session.Id, Text = "Keep the deployment decision", SourceEventId = "one" }, default);
        var task = new Quacktion.App.Models.Quacktion { OwnerAccountId = owner.Id, SessionId = session.Id, Status = QuacktionStatus.PendingApproval, Approval = new() { Status = ApprovalStatus.Pending } };
        await f.Actions.InsertAsync(task, default);
        await f.SessionService.SuspendAsync(owner.Id, session.Id, "stale-provider", default);
        Assert.Equal(SessionStatus.Active, (await f.Sessions.GetAsync(session.Id, default))!.Status);
        await f.SessionService.SuspendAsync(owner.Id, session.Id, session.AgentSessionId, default);
        Assert.Equal(SessionStatus.Suspended, (await f.Sessions.GetAsync(session.Id, default))!.Status);
        var resumed = await f.SessionService.ResumeAsync(owner.Id, session.Id, default);
        Assert.Equal(session.Id, resumed.Id); Assert.Null(resumed.EndedAt);
        Assert.Single(await f.Transcripts.RecentAsync(session.Id, 10, default));
        Assert.Equal(ApprovalStatus.Pending, (await f.Actions.GetAsync(task.Id, default))!.Approval!.Status);
        Assert.Empty(f.Voice.UserTexts);
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.ResumeAsync("other", session.Id, default));
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.ResumeAsync(owner.Id, "invalid", default));
        await f.SessionService.EndAsync(owner.Id, session.Id, default);
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.ResumeAsync(owner.Id, session.Id, default));
        await f.SessionService.SelectResumeAsync(owner.Id, session.Id, default);
        Assert.Equal(session.Id, (await f.SessionService.ResumeAsync(owner.Id, session.Id, default)).Id);
    }
    [MongoFact]
    public async Task DatabaseRestartLeasesFencingJournalIsolationEncryptionAndRecordingGaps()
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO")!;
        var name = "quacktion_resume_test_" + Guid.NewGuid().ToString("N");
        var options = Options.Create(new MongoDbSettings { ConnectionString = uri, Database = name });
        var db = new MongoDbContext(options);
        try
        {
            await db.InitializeAsync(default);
            var session = new Session { OwnerAccountId = "owner", Status = SessionStatus.Active };
            await db.Collection<Session>().InsertOneAsync(session);
            var first = new ConversationPersistence(db);
            await first.AcquireAsync("owner", session.Id, "first", default);
            // A new context/service simulates a backend process using the same durable database.
            var restarted = new ConversationPersistence(new MongoDbContext(options));
            await Assert.ThrowsAsync<AppException>(() => restarted.AcquireAsync("owner", session.Id, "second", default));
            Assert.False(await restarted.RenewAsync("owner", session.Id, "wrong", default));
            await restarted.ReleaseAsync("owner", session.Id, "wrong", default);
            Assert.True(await restarted.IsLeasedAsync("owner", session.Id, default));
            await db.Collection<ConversationState>().UpdateOneAsync(x => x.Id == session.Id, Builders<ConversationState>.Update.Set(x => x.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            await restarted.AcquireAsync("owner", session.Id, "second", default);
            await first.ReleaseAsync("owner", session.Id, "first", default);
            Assert.True(await restarted.RenewAsync("owner", session.Id, "second", default));
            await Assert.ThrowsAsync<AppException>(() => restarted.AcquireAsync("intruder", session.Id, "x", default));
            var entry = new TranscriptEntry { SessionId = session.Id, Text = "A confirmed decision", SourceEventId = "final-1" };
            await first.RecordAsync("owner", session.Id, "TranscriptReceived", entry, default);
            await restarted.RecordAsync("owner", session.Id, "TranscriptReceived", entry, default);
            Assert.Single(await restarted.HistoryAsync("owner", session.Id, 0, 10, default));
            await Assert.ThrowsAsync<AppException>(() => restarted.HistoryAsync("intruder", session.Id, 0, 10, default));
            var cipher = Cipher(); var states = new RealtimeStateRepo(db, cipher);
            var checkpoint = new RealtimeState { Id = session.Id, SessionId = session.Id, OwnerAccountId = "owner", ResumptionHandle = "provider-secret" };
            await states.InsertAsync(checkpoint, default);
            Assert.Null((await db.Collection<RealtimeState>().Find(x => x.Id == session.Id).SingleAsync()).ResumptionHandle);
            Assert.Equal("provider-secret", (await new RealtimeStateRepo(new MongoDbContext(options), cipher).GetAsync(session.Id, default))!.ResumptionHandle);
            var device = new Device { OwnerAccountId = "owner", DeviceIdentifier = "resume-device", Mode = "meeting" };
            var meeting = new Meeting { OwnerAccountId = "owner", DeviceId = device.Id, Name = "Security review" }; device.MeetingId = meeting.Id;
            await db.Collection<Meeting>().InsertOneAsync(meeting); await db.Collection<Device>().InsertOneAsync(device);
            var transports = new DeviceTransportPersistence(db);
            await transports.AcquireAsync(device, "connection1", "process-a", default);
            await Assert.ThrowsAsync<AppException>(() => transports.AcquireAsync(device, "duplicate", "process-b", default));
            await transports.AcquireAsync(device, "replacement", "process-a", default);
            await transports.DisconnectAsync(device, "connection1", default);
            Assert.True(await transports.CheckpointAsync(device, "replacement", default));
            await transports.DisconnectAsync(device, "replacement", default);
            await transports.AcquireAsync(device, "connection2", "process-a", default);
            await transports.AudioReturnedAsync(device, "connection2", default);
            var gap = await db.Collection<MeetingEvent>().Find(x => x.MeetingId == meeting.Id).SingleAsync();
            using (var data = JsonDocument.Parse(gap.PayloadJson)) { Assert.True(data.RootElement.GetProperty("startEstimated").GetBoolean()); Assert.NotEqual(JsonValueKind.Null, data.RootElement.GetProperty("endedAt").ValueKind); }
            Assert.Null((await db.Collection<Meeting>().Find(x => x.Id == meeting.Id).SingleAsync()).EndedAt);
            await db.Collection<DeviceTransportState>().UpdateOneAsync(x => x.Id == device.Id, Builders<DeviceTransportState>.Update.Set(x => x.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            await new DeviceTransportPersistence(new MongoDbContext(options)).AcquireAsync(device, "connection3", "process-c", default);
            var crashGap = await db.Collection<MeetingEvent>().Find(x => x.Id == "gap_connection3").SingleAsync();
            Assert.True(JsonDocument.Parse(crashGap.PayloadJson).RootElement.GetProperty("startEstimated").GetBoolean());
            Assert.False(await transports.CheckpointAsync(device, "connection2", default));
        }
        finally { await new MongoClient(uri).DropDatabaseAsync(name); }
    }
}
