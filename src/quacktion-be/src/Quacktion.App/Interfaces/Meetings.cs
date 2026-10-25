using Quacktion.App.Models;

namespace Quacktion.App.Interfaces;

public interface IMeetingRepo : IRepository<Meeting>;
public interface IMeetingAnalysisRepo : IRepository<MeetingAnalysis>;
public interface IMeetingInsightRepo : IRepository<MeetingInsight>;
public interface IMeetingTranscriptTurnRepo : IRepository<MeetingTranscriptTurn>
{
    Task<IReadOnlyList<MeetingTranscriptTurn>> OrderedAsync(string meetingId, int offset, int limit, CancellationToken ct);
}
public interface IMeetingAudioStore
{
    Task SaveSegmentAsync(MeetingSegment segment, ReadOnlyMemory<byte> pcm, CancellationToken ct);
    Task<(string Path, long Bytes)> FinalizeAsync(string meetingId, IMeetingSegmentRepo segments, CancellationToken ct);
    Task<Stream?> OpenFinalAsync(string meetingId, CancellationToken ct);
    Task DeleteAsync(string meetingId, CancellationToken ct);
}
public interface IFinalMeetingTranscriber
{
    Task<IReadOnlyList<MeetingTranscriptTurn>> TranscribeAsync(Meeting meeting, string wavPath, CancellationToken ct);
}
public interface IMeetingEventRepo : IRepository<MeetingEvent>;
public interface IMeetingSegmentRepo : IRepository<MeetingSegment>
{
    Task<IReadOnlyList<MeetingSegment>> OrderedAsync(string meetingId, int offset, int limit, CancellationToken ct);
}
public interface IMeetingTranscriber
{
    Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);
}
