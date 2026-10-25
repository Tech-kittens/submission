using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Preparation;
namespace Quacktion.Web.Infrastructure;

public sealed class DeviceNotificationWorker(IServiceScopeFactory scopes, DatabaseReadiness readiness,
    ILogger<DeviceNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                if (!readiness.Ready) continue;
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                await LifecycleAsync(sp.GetRequiredService<IMongoDbContext>(), sp.GetRequiredService<IDeviceNotificationService>(), stoppingToken);
                await sp.GetRequiredService<DeviceNotificationService>().TickAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning("DeviceNotificationDispatchDeferred {ErrorType}", e.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public static async Task LifecycleAsync(IMongoDbContext db, IDeviceNotificationService notifications, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tasks = await db.Collection<Quacktion.App.Models.Quacktion>().Find(x =>
            (x.Status == QuacktionStatus.Completed || x.Status == QuacktionStatus.Failed || x.Status == QuacktionStatus.Rejected || x.Status == QuacktionStatus.PendingApproval)
            && (x.NotificationStatus != x.Status || x.Status == QuacktionStatus.PendingApproval && x.NotificationAt < now.AddSeconds(-90)))
            .SortBy(x => x.CreatedAt).Limit(50).ToListAsync(ct);
        foreach (var task in tasks)
        {
            var eventAt = task.History.Where(x => x.FinishedAt.HasValue).Select(x => x.FinishedAt!.Value)
                .Append(task.Approval?.ResolvedAt ?? task.CreatedAt).Max();
            var expiresAt = task.Status == QuacktionStatus.PendingApproval
                ? (task.Approval?.ExpiresAt is { } approvalExpiry && approvalExpiry < now.AddMinutes(2) ? approvalExpiry : now.AddMinutes(2))
                : eventAt.AddMinutes(10);
            var session = await db.Collection<Session>().Find(x => x.Id == task.SessionId && x.OwnerAccountId == task.OwnerAccountId).FirstOrDefaultAsync(ct);
            if (expiresAt > now && session?.DeviceId is { } device && await db.Collection<Device>().Find(x => x.Id == device && x.OwnerAccountId == task.OwnerAccountId && x.CredentialHash != null).AnyAsync(ct))
            {
                var kind = task.Status == QuacktionStatus.Completed ? "action_done" : task.Status == QuacktionStatus.PendingApproval ? "needs_confirmation" : "failed";
                var cycle = task.Status == QuacktionStatus.PendingApproval ? (now.Ticks / TimeSpan.FromSeconds(90).Ticks).ToString() : "terminal";
                await notifications.NotifyAsync(device, new() { NotifyId = Secrets.Digest(task.Id, task.Status + cycle + ":" + task.NotificationGeneration)[..40],
                    OwnerAccountId = task.OwnerAccountId, QuacktionId = task.Id, QuacktionStatus = task.Status.ToString(), Kind = kind, ExpiresAt = expiresAt }, ct);
            }
            await db.Collection<Quacktion.App.Models.Quacktion>().UpdateOneAsync(x => x.Id == task.Id && x.Status == task.Status,
                Builders<Quacktion.App.Models.Quacktion>.Update.Set(x => x.NotificationStatus, task.Status).Set(x => x.NotificationAt, now), cancellationToken: ct);
        }
    }
}
