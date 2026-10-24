using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.Data.MongoDB;
using Microsoft.Extensions.Options;
namespace Quacktion.Web.Infrastructure;

public sealed class SessionLifecycleSettings { public int InactiveTimeoutSeconds { get; set; } = 1800; }

public sealed class DatabaseReadiness { public volatile bool Ready; }
public sealed class DatabaseWorker(IMongoDbContext db, DatabaseReadiness readiness, IServiceScopeFactory scopes, ILogger<DatabaseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await db.InitializeAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepo>();
                while (true)
                {
                    var batch = await sessions.ListAsync(s => s.Status == SessionStatus.Starting || s.Status == SessionStatus.Active || s.Status == SessionStatus.Ending, 0, 100, stoppingToken);
                    if (batch.Count == 0) break;
                    foreach (var s in batch) { s.Status = SessionStatus.Failed; s.FailureCode = "transport_host_restarted"; s.EndedAt = DateTime.UtcNow; await sessions.ReplaceAsync(s, stoppingToken); }
                }
                readiness.Ready = true; return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("MongoDB initialization unavailable {ErrorType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
public sealed class ExecutionWorker(IServiceScopeFactory scopes, DatabaseReadiness readiness, ILogger<ExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (readiness.Ready)
                {
                    using var scope = scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<IQuacktionExecutionService>().ExecuteNextAsync(stoppingToken)) continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogError("Execution worker error {ErrorType}", e.GetType().Name); }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
/// <summary>Recovery for the single transport host; tasks remain independently durable.</summary>
public sealed class MaintenanceWorker(IServiceScopeFactory scopes, DatabaseReadiness readiness, IOptions<SessionLifecycleSettings> lifecycleSettings, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (readiness.Ready)
                {
                    using var scope = scopes.CreateScope(); var sp = scope.ServiceProvider;
                    var sessions = sp.GetRequiredService<ISessionRepo>(); var devices = sp.GetRequiredService<IDeviceRepo>();
                    var accounts = sp.GetRequiredService<IAccountRepo>(); var actions = sp.GetRequiredService<IQuacktionRepo>();
                    var connections = sp.GetRequiredService<IDeviceConnectionManager>();
                    for (var offset = 0; ; offset += 100)
                    {
                        var batch = await devices.ListAsync(d => true, offset, 100, ct); if (batch.Count == 0) break;
                        foreach (var d in batch)
                        {
                            var owner = await accounts.GetAsync(d.OwnerAccountId, ct);
                            var changed = false;
                            if (owner is not { IsActive: true }) { d.CredentialHash = null; d.Status = DeviceStatus.Unpaired; await connections.DisconnectAsync(d.Id, ct); changed = true; }
                            else if (!connections.IsConnected(d.Id) && d.Status == DeviceStatus.Online) { d.Status = DeviceStatus.Offline; changed = true; }
                            if (d.ActiveSessionId is not null)
                            {
                                var s = await sessions.GetAsync(d.ActiveSessionId, ct);
                                if (s is null || s.Status is SessionStatus.Completed or SessionStatus.Failed || owner is not { IsActive: true }) { d.ActiveSessionId = null; changed = true; }
                            }
                            if (changed) await devices.ReplaceAsync(d, ct);
                        }
                    }
                    for (var offset = 0; ; offset += 100)
                    {
                        var batch = await sessions.ListAsync(s => s.Status == SessionStatus.Active || s.Status == SessionStatus.Starting || s.Status == SessionStatus.Ending, offset, 100, ct);
                        if (batch.Count == 0) break;
                        foreach (var s in batch)
                        {
                            var accountActive = await accounts.GetAsync(s.OwnerAccountId, ct) is { IsActive: true };
                            if (accountActive && s.Status == SessionStatus.Active)
                            {
                                var seconds = lifecycleSettings.Value.InactiveTimeoutSeconds;
                                if (seconds > 0 && s.UpdatedAt <= DateTime.UtcNow.AddSeconds(-seconds))
                                    await sp.GetRequiredService<ISessionService>().EndAsync(s.OwnerAccountId, s.Id, ct);
                                continue;
                            }
                            if (accountActive && s.UpdatedAt > DateTime.UtcNow.AddMinutes(-2)) continue;
                            if (s.AgentSessionId is not null) { try { await sp.GetRequiredService<IConversationalAgentGateway>().EndSessionAsync(s.AgentSessionId, ct); } catch (Exception e) { logger.LogWarning("Deleted account voice cleanup {ErrorType}", e.GetType().Name); } }
                            s.Status = SessionStatus.Failed; s.FailureCode = accountActive ? "lifecycle_timeout" : "account_deleted"; s.EndedAt = DateTime.UtcNow; await sessions.ReplaceAsync(s, ct);
                        }
                    }
                    for (var offset = 0; ; offset += 100)
                    {
                        var batch = await actions.ListAsync(q => true, offset, 100, ct); if (batch.Count == 0) break;
                        foreach (var q in batch)
                        {
                            if ((q.Status is QuacktionStatus.PendingApproval or QuacktionStatus.Queued or QuacktionStatus.InProgress) && await accounts.GetAsync(q.OwnerAccountId, ct) is not { IsActive: true })
                            { q.CancellationRequested = true; if (q.Status != QuacktionStatus.InProgress) q.Status = QuacktionStatus.Cancelled; await actions.ReplaceAsync(q, ct); }
                            await actions.SyncAuditAsync(q, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Maintenance deferred {ErrorType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }
}

public sealed class TransportShutdownService(IServiceScopeFactory scopes, ILogger<TransportShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepo>();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            while (!ct.IsCancellationRequested)
            {
                var batch = await repo.ListAsync(s => s.Status == SessionStatus.Active, 0, 100, ct);
                if (batch.Count == 0) return;
                foreach (var session in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    await sessions.EndAsync(session.OwnerAccountId, session.Id, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { logger.LogWarning("Transport shutdown deferred to restart recovery {ErrorType}", e.GetType().Name); }
    }
}
