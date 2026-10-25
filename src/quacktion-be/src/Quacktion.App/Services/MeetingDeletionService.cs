using Quacktion.App.Interfaces;
using Quacktion.App.Models;

namespace Quacktion.App.Services;

public sealed class MeetingDeletionService(IMeetingRepo meetings, IMeetingSegmentRepo segments,
    IMeetingEventRepo events, IMeetingAnalysisRepo analyses, IMeetingInsightRepo insights,
    IMeetingTranscriptTurnRepo turns, IMeetingAudioStore audio, IMeetingMemoryLifecycle? memory = null)
{
    public async Task DeleteAsync(Meeting meeting, CancellationToken ct)
    {
        Guard.State(meeting.EndedAt is not null && meeting.Status is MeetingStatus.Completed or MeetingStatus.Failed or MeetingStatus.Cancelled,
            "meeting_still_active");
        while (true)
        {
            var page = await segments.ListAsync(s => s.MeetingId == meeting.Id, 0, 100, ct);
            if (page.Count == 0) break;
            foreach (var segment in page) await segments.DeleteAsync(segment, ct);
        }
        while (true)
        {
            var page = await turns.ListAsync(t => t.MeetingId == meeting.Id, 0, 100, ct);
            if (page.Count == 0) break;
            foreach (var turn in page) await turns.DeleteAsync(turn, ct);
        }
        while (true)
        {
            var page = await events.ListAsync(e => e.MeetingId == meeting.Id, 0, 100, ct);
            if (page.Count == 0) break;
            foreach (var entry in page) await events.DeleteAsync(entry, ct);
        }
        var analysis = await analyses.FindAsync(a => a.MeetingId == meeting.Id, ct);
        if (analysis is not null) await analyses.DeleteAsync(analysis, ct);
        while (true)
        {
            var page = await insights.ListAsync(i => i.MeetingId == meeting.Id, 0, 100, ct);
            if (page.Count == 0) break;
            foreach (var insight in page) await insights.DeleteAsync(insight, ct);
        }
        await audio.DeleteAsync(meeting.Id, ct);
        await meetings.DeleteAsync(meeting, ct);
        if (memory is not null) await memory.DeleteAsync(meeting.OwnerAccountId, meeting.Id, ct);
    }
}
