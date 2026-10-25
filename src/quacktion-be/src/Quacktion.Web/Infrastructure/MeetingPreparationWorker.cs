using Microsoft.Extensions.Options;
using Quacktion.Data.Preparation;
namespace Quacktion.Web.Infrastructure;

public sealed class MeetingPreparationWorker(IServiceScopeFactory scopes, DatabaseReadiness readiness,
    IOptions<PreparationSettings> settings, ILogger<MeetingPreparationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Independent loops: slow calendar/embedding work cannot delay briefing dispatch or Live audio.
        await Task.WhenAll(Run("calendar", async sp => await sp.GetRequiredService<CalendarPreparationSync>().TickAsync(ct), ct),
            Run("delivery", async sp =>
            {
                for (var i = 0; i < 20; i++) await sp.GetRequiredService<PreparationDeliveryWorker>().TickAsync(ct);
            }, ct),
            Run("research", async sp => await sp.GetRequiredService<MeetingPreparationResearchWorker>().TickAsync(ct), ct),
            Run("memory", async sp => await sp.GetRequiredService<MeetingMemoryIngestion>().TickAsync(ct), ct));
    }
    private async Task Run(string stage, Func<IServiceProvider, Task> action, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (readiness.Ready && (stage == "memory" ? settings.Value.MemoryEnabled : settings.Value.Enabled))
                { await using var scope = scopes.CreateAsyncScope(); await action(scope.ServiceProvider); }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            { logger.LogWarning("MeetingPreparationWorkerFailed {Stage} {ErrorType}", stage, e.GetType().Name); }
            if (!await timer.WaitForNextTickAsync(ct)) break;
        }
    }
}
