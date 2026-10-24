using Microsoft.Extensions.Logging;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.App.Services;

public sealed class QuacktionExecutionService(IQuacktionRepo repo, IAccountRepo accounts, IActionRiskEvaluator risk,
    IAgentOrchestrator agents, ISessionEvents events, ILogger<QuacktionExecutionService> logger) : IQuacktionExecutionService
{
    public async Task<bool> ExecuteNextAsync(CancellationToken ct)
    {
        var q = await repo.ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), ct);
        if (q is null) return false;
        var lease = q.LeaseId!;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(TimeSpan.FromMinutes(2));
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = Monitor(q.Id, lease, operation, monitoring.Token);
        AgentTaskResult result;
        try
        {
            var account = await accounts.GetAsync(q.OwnerAccountId, ct);
            if (q.CancellationRequested || account is not { IsActive: true }) result = new(false, null, "cancelled");
            else if (!risk.IsSafe(q.ActionType) && !QuacktionService.ValidApproval(q)) result = new(false, null, "approval_invalid");
            else
            {
                await events.PublishAsync(q.OwnerAccountId, q.SessionId, "QuacktionStatusChanged", new { q.Id, q.Status, q.Attempts }, ct);
                result = await agents.ExecuteAsync(new(q.ActionType, q.ParametersJson, q.IdempotencyKey, q.OwnerAccountId, q.SessionId), operation.Token).WaitAsync(operation.Token);
            }
        }
        catch (OperationCanceledException) { result = new(false, null, "interrupted", true); }
        catch (Exception e) { logger.LogError("Execution failed {QuacktionId} {ErrorType}", q.Id, e.GetType().Name); result = new(false, null, "agent_failure", e is HttpRequestException or TimeoutException); }
        finally { await monitoring.CancelAsync(); try { await monitor; } catch (OperationCanceledException) { } }
        // Host shutdown leaves the lease to expire; restart recovery uses the same idempotency key.
        if (ct.IsCancellationRequested) return true;
        var current = await repo.GetAsync(q.Id, ct);
        if (current?.LeaseId != lease || current.LeaseExpiresAt <= DateTime.UtcNow) return true;
        var owner = await accounts.GetAsync(current.OwnerAccountId, ct);
        if (current.CancellationRequested || owner is not { IsActive: true } || result.FailureCode == "cancelled") current.Status = QuacktionStatus.Cancelled;
        else if (result.Success) current.Status = QuacktionStatus.Completed;
        else if (result.Transient && current.Attempts < 3)
        { current.Status = QuacktionStatus.Queued; current.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, current.Attempts) * 5); }
        else current.Status = QuacktionStatus.Failed;
        if (result.ResultJson?.Length > 524288) { result = new(false, null, "agent_result_too_large"); current.Status = QuacktionStatus.Failed; }
        current.ResultJson = result.Success ? result.ResultJson : null; current.FailureCode = result.FailureCode;
        var execution = current.History.Last(); execution.FinishedAt = DateTime.UtcNow; execution.Outcome = current.Status.ToString(); execution.FailureCode = result.FailureCode;
        if (await repo.FinishAsync(current, lease, ct))
        {
            logger.LogInformation("QuacktionExecuted {AccountId} {QuacktionId} {Attempt} {Status}", current.OwnerAccountId, current.Id, current.Attempts, current.Status);
            await repo.SyncAuditAsync(current, ct);
            await events.PublishAsync(current.OwnerAccountId, current.SessionId, "QuacktionStatusChanged", current, ct);
            if (current.Status == QuacktionStatus.Completed && current.ActionType == "internal.research")
                await events.PublishAsync(current.OwnerAccountId, current.SessionId, "ResearchCompleted", new { current.Id, current.ResultJson }, ct);
        }
        return true;
    }
    private async Task Monitor(string id, string lease, CancellationTokenSource operation, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var q = await repo.GetAsync(id, ct);
                var owner = q is null ? null : await accounts.GetAsync(q.OwnerAccountId, ct);
                if (q is null || q.CancellationRequested || owner is not { IsActive: true } || !await repo.RenewAsync(id, lease, DateTime.UtcNow.AddSeconds(30), ct))
                { await operation.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { await operation.CancelAsync(); }
    }
}
