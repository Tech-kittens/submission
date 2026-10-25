using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public sealed class MeetingMemoryIngestion(IMongoDbContext db, IMeetingRepo meetings, IMeetingAnalysisRepo analyses,
    IMeetingTranscriptTurnRepo turns, IMeetingSegmentRepo segments, IMeetingEmbeddingService embedding,
    IOptions<PreparationSettings> settings, ILogger<MeetingMemoryIngestion>? logger = null) : IMeetingMemoryLifecycle
{
    public static IEnumerable<string> Chunk(string text, int size = 2400)
    {
        while (text.Length > size)
        {
            var split = text.LastIndexOf(' ', size, size / 2);
            if (split < size / 2) split = size;
            yield return text[..split]; text = text[split..].TrimStart();
        }
        if (!string.IsNullOrWhiteSpace(text)) yield return text;
    }
    public async Task IndexAsync(string owner, string meetingId, CancellationToken ct)
    {
        var meeting = Guard.Found(await meetings.FindAsync(x => x.Id == meetingId && x.OwnerAccountId == owner, ct));
        Guard.State(meeting.Status == MeetingStatus.Completed, "meeting_analysis_not_final");
        var analysis = Guard.Found(await analyses.FindAsync(x => x.MeetingId == meetingId && x.OwnerAccountId == owner, ct));
        var memory = db.Collection<MeetingMemoryDocument>();
        var records = new List<(string Type, string Text, string[] Sources)>();
        records.Add(("summary", analysis.Summary, []));
        foreach (var (type, findings) in new[] { ("decision", analysis.Decisions), ("action", analysis.ActionItems),
            ("risk", analysis.Risks), ("question", analysis.UnresolvedQuestions) })
            records.AddRange(findings.Select(x => (type, $"{x.Text}\nStatus: {x.Status}" + (x.Deadline is null ? "" : $"\nDeadline: {x.Deadline}"), x.EvidenceSegmentIds.ToArray())));
        for (var offset = 0; ; offset += 100)
        {
            if (meeting.FinalTranscriptStatus == "Ready")
            {
                var page = await turns.OrderedAsync(meetingId, offset, 100, ct);
                records.AddRange(page.Select(x => ("transcript", (x.SpeakerName is null ? "" : x.SpeakerName + ": ") + x.Text, new[] { x.Id })));
                if (page.Count < 100) break;
            }
            else
            {
                var page = await segments.OrderedAsync(meetingId, offset, 100, ct);
                records.AddRange(page.Where(x => x.Transcript is not null).Select(x => ("transcript", x.Transcript!, new[] { x.SegmentId })));
                if (page.Count < 100) break;
            }
        }
        var generation = Secrets.Digest("memory-v1", JsonSerializer.Serialize(analysis) + JsonSerializer.Serialize(meeting.ParticipantEmails)
            + string.Join("\n", records.Select(x => x.Type + "\n" + x.Text + "\n" + string.Join(',', x.Sources))) + embedding.Model + embedding.Dimensions);
        if (meeting.MemoryGeneration == generation) return;
        foreach (var record in records.DistinctBy(x => (x.Type, x.Text)))
        {
            var index = 0;
            foreach (var chunk in Chunk(record.Text))
            {
                var id = Secrets.Digest(meetingId, generation + record.Type + record.Text + index++);
                if (await memory.Find(x => x.Id == id).AnyAsync(ct)) continue;
                var doc = new MeetingMemoryDocument { Id = id, OwnerAccountId = owner, MeetingId = meetingId,
                    MeetingTitle = meeting.Name, MeetingDate = meeting.CreatedAt, MeetingType = meeting.Type.ToString(),
                    Type = record.Type, Content = chunk, SourceSegmentIds = record.Sources, Generation = generation,
                    ParticipantEmails = meeting.ParticipantEmails.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray(),
                    Embedding = await embedding.GenerateEmbeddingAsync(chunk, ct), EmbeddingModel = embedding.Model, EmbeddingDimensions = embedding.Dimensions };
                try { await memory.InsertOneAsync(doc, cancellationToken: ct); }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            }
        }
        // Recheck deletion/ownership before publication; orphan cleanup also runs after interrupted deletion.
        var current = await meetings.FindAsync(x => x.Id == meetingId && x.OwnerAccountId == owner, ct);
        if (current is null) { await DeleteAsync(owner, meetingId, ct); return; }
        await memory.UpdateManyAsync(x => x.MeetingId == meetingId && x.OwnerAccountId == owner && x.Generation != generation,
            Builders<MeetingMemoryDocument>.Update.Set(x => x.Active, false), cancellationToken: ct);
        await memory.UpdateManyAsync(x => x.MeetingId == meetingId && x.OwnerAccountId == owner && x.Generation == generation,
            Builders<MeetingMemoryDocument>.Update.Set(x => x.Active, true), cancellationToken: ct);
        current.MemoryGeneration = generation; current.MemoryIndexedAt = DateTime.UtcNow;
        current.MemoryNextAttemptAt = null; current.MemoryAttempts = 0; current.MemoryFailureCode = null;
        await Guard.Save(meetings, current, ct);
        await memory.DeleteManyAsync(x => x.MeetingId == meetingId && x.OwnerAccountId == owner && x.Generation != generation, ct);
    }
    public Task DeleteAsync(string owner, string meetingId, CancellationToken ct) =>
        db.Collection<MeetingMemoryDocument>().DeleteManyAsync(x => x.OwnerAccountId == owner && x.MeetingId == meetingId, ct);

    public async Task CleanupAsync(CancellationToken ct)
    {
        // Recover memory left behind by a process crash during deletion/retention.
        var orphans = await db.Collection<MeetingMemoryDocument>().Aggregate<BsonDocument>(new BsonDocument[] {
            new("$lookup", new BsonDocument { { "from", "meetings" }, { "localField", "MeetingId" }, { "foreignField", "_id" }, { "as", "source" } }),
            new("$match", new BsonDocument("source", new BsonDocument("$size", 0))),
            new("$limit", 100), new("$project", new BsonDocument { { "_id", 1 }, { "OwnerAccountId", 1 } })
        }, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(10) }).ToListAsync(ct);
        foreach (var orphan in orphans)
            await db.Collection<MeetingMemoryDocument>().DeleteOneAsync(x => x.Id == orphan["_id"].AsString
                && x.OwnerAccountId == orphan["OwnerAccountId"].AsString, ct);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (!settings.Value.MemoryEnabled) return;
        var now = DateTime.UtcNow;
        var batch = await meetings.ListAsync(x => x.Status == MeetingStatus.Completed
            && (x.MemoryIndexedAt == null || x.MemoryIndexedAt < x.ProcessedAt)
            && (x.MemoryNextAttemptAt == null || x.MemoryNextAttemptAt <= now), 0, 5, ct);
        foreach (var meeting in batch)
        {
            meeting.MemoryNextAttemptAt = DateTime.UtcNow.AddMinutes(10);
            if (!await meetings.ReplaceAsync(meeting, ct)) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(8));
                await IndexAsync(meeting.OwnerAccountId, meeting.Id, timeout.Token);
                logger?.LogInformation("MeetingMemoryIndexed {MeetingId}", meeting.Id);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                meeting.MemoryAttempts++;
                meeting.MemoryFailureCode = e is AppException app ? app.Code : "meeting_memory_ingestion_failed";
                logger?.LogWarning("MeetingMemoryIngestionFailed {MeetingId} {FailureCode} {Attempt}", meeting.Id, meeting.MemoryFailureCode, meeting.MemoryAttempts);
                meeting.MemoryNextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(360, Math.Pow(2, Math.Min(9, meeting.MemoryAttempts))));
                await meetings.ReplaceAsync(meeting, ct);
            }
        }
    }
}
