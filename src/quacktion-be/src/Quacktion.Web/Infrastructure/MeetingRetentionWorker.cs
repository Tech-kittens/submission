using Microsoft.Extensions.Options;
using Quacktion.Data.Preparation;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Web.Infrastructure;

public sealed class MeetingRetentionWorker(IServiceScopeFactory scopes, DatabaseReadiness readiness,
    IOptions<MeetingAnalysisSettings> settings, ILogger<MeetingRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (readiness.Ready && settings.Value.RetentionDays > 0)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var meetings = scope.ServiceProvider.GetRequiredService<IMeetingRepo>();
                    var deletion = scope.ServiceProvider.GetRequiredService<MeetingDeletionService>();
                    if (scope.ServiceProvider.GetService<MeetingMemoryIngestion>() is { } memory)
                        await memory.CleanupAsync(ct);
                    var before = DateTime.UtcNow.AddDays(-settings.Value.RetentionDays);
                    while (true)
                    {
                        var batch = await meetings.ListAsync(m => (m.Status == MeetingStatus.Completed || m.Status == MeetingStatus.Failed)
                            && m.EndedAt != null && m.EndedAt < before, 0, 100, ct);
                        if (batch.Count == 0) break;
                        foreach (var meeting in batch) await deletion.DeleteAsync(meeting, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Meeting retention cleanup failed {ErrorType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}
