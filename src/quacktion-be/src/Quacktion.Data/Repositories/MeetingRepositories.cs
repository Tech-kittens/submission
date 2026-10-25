using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.Data.MongoDB;

namespace Quacktion.Data.Repositories;

public sealed class MeetingRepo(IMongoDbContext db) : MongoRepo<Meeting>(db), IMeetingRepo;
public sealed class MeetingAnalysisRepo(IMongoDbContext db) : MongoRepo<MeetingAnalysis>(db), IMeetingAnalysisRepo;
public sealed class MeetingInsightRepo(IMongoDbContext db) : MongoRepo<MeetingInsight>(db), IMeetingInsightRepo;
public sealed class MeetingTranscriptTurnRepo(IMongoDbContext db) : MongoRepo<MeetingTranscriptTurn>(db), IMeetingTranscriptTurnRepo
{
    public async Task<IReadOnlyList<MeetingTranscriptTurn>> OrderedAsync(string meetingId, int offset, int limit, CancellationToken ct) =>
        await Collection.Find(t => t.MeetingId == meetingId).SortBy(t => t.ChunkIndex).ThenBy(t => t.StartMs)
            .ThenBy(t => t.Id).Skip(offset).Limit(limit).ToListAsync(ct);
}
public sealed class MeetingEventRepo(IMongoDbContext db) : MongoRepo<MeetingEvent>(db), IMeetingEventRepo;
public sealed class MeetingSegmentRepo(IMongoDbContext db) : MongoRepo<MeetingSegment>(db), IMeetingSegmentRepo
{
    public async Task<IReadOnlyList<MeetingSegment>> OrderedAsync(string meetingId, int offset, int limit, CancellationToken ct) =>
        await Collection.Find(s => s.MeetingId == meetingId).SortBy(s => s.BootId).ThenBy(s => s.StartUptimeMs)
            .ThenBy(s => s.SegmentId).Skip(offset).Limit(limit).ToListAsync(ct);
}
