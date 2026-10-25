using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Microsoft.Extensions.Options;

namespace Quacktion.Web.Infrastructure;

public sealed class MeetingAnalysisSettings
{
    public int WindowIntervalSeconds { get; set; } = 120;
    public int RetentionDays { get; set; } = 30;
}

public sealed class MeetingAnalysisWorker(IServiceScopeFactory scopes, DatabaseReadiness readiness,
    IOptions<MeetingAnalysisSettings> settings, ILogger<MeetingAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (readiness.Ready)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sp = scope.ServiceProvider;
                    var meetings = sp.GetRequiredService<IMeetingRepo>();
                    var segments = sp.GetRequiredService<IMeetingSegmentRepo>();
                    var turns = sp.GetRequiredService<IMeetingTranscriptTurnRepo>();
                    var audio = sp.GetRequiredService<IMeetingAudioStore>();
                    var finalTranscriber = sp.GetRequiredService<IFinalMeetingTranscriber>();
                    var intelligence = sp.GetRequiredService<GeminiMeetingIntelligence>();
                    var dueBefore = DateTime.UtcNow.AddSeconds(-settings.Value.WindowIntervalSeconds);
                    var active = await meetings.ListAsync(m => m.Status == MeetingStatus.Active
                        && (m.LastInsightAttemptAt == null || m.LastInsightAttemptAt <= dueBefore), 0, 10, ct);
                    foreach (var meeting in active)
                    {
                        meeting.LastInsightAttemptAt = DateTime.UtcNow;
                        if (!await meetings.ReplaceAsync(meeting, ct)) continue;
                        try { await intelligence.AnalyseWindowAsync(meeting, ct); }
                        catch (Exception e) when (e is not OperationCanceledException)
                        { logger.LogWarning("Incremental meeting analysis unavailable {MeetingId} {ErrorType}", meeting.Id, e.GetType().Name); }
                    }
                    var now = DateTime.UtcNow;
                    var batch = await meetings.ListAsync(m => (m.Status == MeetingStatus.Ending || m.Status == MeetingStatus.Processing)
                        && (m.NextAnalysisAttemptAt == null || m.NextAnalysisAttemptAt <= now), 0, 10, ct);
                    foreach (var meeting in batch)
                    {
                        // The device protocol permits a 30-second segment already in flight at end time.
                        if (meeting.EndedAt is null || DateTime.UtcNow - meeting.EndedAt < TimeSpan.FromSeconds(35)) continue;
                        if (meeting.NextAnalysisAttemptAt is { } retryAt && retryAt > DateTime.UtcNow) continue;
                        // Final file transcription is independent of provisional per-segment transcription.
                        if (meeting.Status == MeetingStatus.Ending)
                        {
                            meeting.Status = MeetingStatus.Processing;
                            if (!await meetings.ReplaceAsync(meeting, ct)) continue;
                        }
                        try
                        {
                            var (path, bytes) = await audio.FinalizeAsync(meeting.Id, segments, ct);
                            meeting.RecordingBytes = bytes;
                            meeting.RecordingStatus = "Ready";
                            if (bytes > 44 && meeting.FinalTranscriptStatus != "Ready")
                            {
                                var finalTurns = await finalTranscriber.TranscribeAsync(meeting, path, ct);
                                foreach (var turn in finalTurns)
                                {
                                    try { await turns.InsertAsync(turn, ct); }
                                    catch (AppException e) when (e.Code == "duplicate_key") { }
                                }
                                meeting.FinalTranscriptStatus = "Ready";
                            }
                            await Guard.Save(meetings, meeting, ct);
                            await intelligence.AnalyseAsync(meeting, ct);
                            meeting.Status = MeetingStatus.Completed;
                            meeting.ProcessedAt = DateTime.UtcNow;
                            meeting.AnalysisFailureCode = null;
                            meeting.NextAnalysisAttemptAt = null;
                            await meetings.ReplaceAsync(meeting, ct);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            if (meeting.RecordingStatus != "Ready") meeting.RecordingStatus = "Error";
                            logger.LogWarning("Meeting analysis failed {MeetingId} {ErrorType}", meeting.Id, e.GetType().Name);
                            meeting.AnalysisFailureCode = e is AppException app ? app.Code : "meeting_analysis_failed";
                            meeting.AnalysisAttemptCount++;
                            meeting.Status = meeting.AnalysisAttemptCount >= 5 ? MeetingStatus.Failed : MeetingStatus.Processing;
                            meeting.NextAnalysisAttemptAt = meeting.Status == MeetingStatus.Processing
                                ? DateTime.UtcNow.AddSeconds(Math.Min(300, 10 * Math.Pow(2, meeting.AnalysisAttemptCount))) : null;
                            await meetings.ReplaceAsync(meeting, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Meeting worker error {ErrorType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
