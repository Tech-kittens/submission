using Microsoft.Extensions.Logging;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.App.Services;

public sealed class QuacktionExecutionService(IQuacktionRepo repo, IAccountRepo accounts, IActionRiskEvaluator risk,
    IAgentOrchestrator agents, ISessionEvents events, ILogger<QuacktionExecutionService> logger,
    IMcpSkillExecutionRepo? skillExecutions = null, IMcpSkillExecutor? skillExecutor = null) : IQuacktionExecutionService
{
    public async Task<bool> ExecuteNextAsync(CancellationToken ct)
    {
        await ResolveWaitingAsync(ct);
        var q = await repo.ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), ct);
        if (q is null) return false;
        var lease = q.LeaseId!;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(TimeSpan.FromMinutes(q.ActionType is "mcp.request" or "internal.mcp_read" ? 5 : 2));
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = Monitor(q.Id, lease, operation, monitoring.Token);
        AgentTaskResult result;
        try
        {
            var account = await accounts.GetAsync(q.OwnerAccountId, ct);
            if (q.CancellationRequested || account is not { IsActive: true }) result = new(false, null, "cancelled");
            else if (!risk.IsSafe(q.ActionType) && !QuacktionService.ValidApproval(q)
                && !QuacktionService.AuthorizedMcp(q)) result = new(false, null, "approval_invalid");
            else
            {
                await events.PublishAsync(q.OwnerAccountId, q.SessionId, "QuacktionStatusChanged", new { q.Id, q.Status, q.Attempts }, ct);
                result = await agents.ExecuteAsync(new(q.ActionType, q.ParametersJson, q.IdempotencyKey, q.OwnerAccountId, q.SessionId), operation.Token).WaitAsync(operation.Token);
            }
        }
        catch (OperationCanceledException) { result = new(false, null, "interrupted", q.ActionType != "mcp.invoke"); }
        catch (Exception e) { logger.LogError("Execution failed {QuacktionId} {ErrorType}", q.Id, e.GetType().Name); result = new(false, null, "agent_failure", q.ActionType != "mcp.invoke" && e is HttpRequestException or TimeoutException); }
        finally { await monitoring.CancelAsync(); try { await monitor; } catch (OperationCanceledException) { } }
        // Host shutdown leaves the lease to expire; restart recovery uses the same idempotency key.
        if (ct.IsCancellationRequested) return true;
        var current = await repo.GetAsync(q.Id, ct);
        if (current?.LeaseId != lease || current.LeaseExpiresAt <= DateTime.UtcNow) return true;
        var owner = await accounts.GetAsync(current.OwnerAccountId, ct);
        if (current.CancellationRequested || owner is not { IsActive: true } || result.FailureCode == "cancelled") current.Status = QuacktionStatus.Cancelled;
        else if (result.Success)
        {
            current.ChildTaskIds = result.PendingTaskIds?.Distinct().ToList() ?? [];
            current.Status = current.ChildTaskIds.Count > 0 ? QuacktionStatus.WaitingForTools : QuacktionStatus.Completed;
        }
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
    private async Task ResolveWaitingAsync(CancellationToken ct)
    {
        var offset = 0;
        while (true)
        {
        var waiting = await repo.ListAsync(x => x.Status == QuacktionStatus.WaitingForTools, offset, 100, ct);
        if (waiting.Count == 0) break;
        foreach (var parent in waiting)
        {
            if (skillExecutions is not null)
            {
                var pendingSkills = await skillExecutions.ListAsync(x => x.ParentTaskId == parent.Id
                    && x.UserId == parent.OwnerAccountId && x.Status == "AwaitingApproval", 0, 100, ct);
                var missingIds = pendingSkills.Select(x => x.ApprovalTaskId).Where(x => x is not null
                    && !parent.ChildTaskIds.Contains(x, StringComparer.Ordinal)).Cast<string>().ToArray();
                if (missingIds.Length > 0)
                {
                    parent.ChildTaskIds.AddRange(missingIds);
                    await Guard.Save(repo, parent, ct);
                    offset++;
                    continue;
                }
            }
            var children = new List<global::Quacktion.App.Models.Quacktion>();
            foreach (var id in parent.ChildTaskIds)
            {
                var child = await repo.FindAsync(x => x.Id == id && x.OwnerAccountId == parent.OwnerAccountId && x.SessionId == parent.SessionId, ct);
                if (child is not null) children.Add(child);
            }
            var terminalFailure = children.Count != parent.ChildTaskIds.Count || children.Any(x => x.Status is QuacktionStatus.Failed or QuacktionStatus.Rejected or QuacktionStatus.Cancelled);
            if (!terminalFailure && children.Any(x => x.Status != QuacktionStatus.Completed)) { offset++; continue; }
            var skillRecords = new List<McpSkillExecution>();
            if (skillExecutions is not null)
                foreach (var child in children)
                    if (await skillExecutions.FindAsync(x => x.ApprovalTaskId == child.Id && x.UserId == parent.OwnerAccountId, ct) is { } skill)
                        skillRecords.Add(skill);
            var resumedWorkflow = false;
            var newChildProposed = false;
            string? continuationFailure = null;
            if (!terminalFailure && skillExecutor is not null)
                foreach (var workflow in skillRecords.Where(x => x.SkillId.StartsWith("workflow.", StringComparison.Ordinal)
                    && x.Status == "AwaitingApproval"))
                {
                    SkillExecutionResult continuation;
                    try { continuation = await skillExecutor.ResumeAsync(workflow.Id, parent.OwnerAccountId, parent.Id, ct); }
                    catch (Exception e) when (e is AppException or HttpRequestException or TimeoutException)
                    {
                        terminalFailure = true;
                        continuationFailure = e is AppException app ? app.Code : "workflow_resume_failed";
                        break;
                    }
                    resumedWorkflow = true;
                    if (continuation.Status == "awaiting_approval" && continuation.ApprovalTaskId is { } nextChild)
                    {
                        if (!parent.ChildTaskIds.Contains(nextChild, StringComparer.Ordinal)) parent.ChildTaskIds.Add(nextChild);
                        await Guard.Save(repo, parent, ct);
                        await events.PublishAsync(parent.OwnerAccountId, parent.SessionId, "QuacktionStatusChanged", parent, ct);
                        newChildProposed = true;
                        break;
                    }
                    if (continuation.Status == "partially_failed")
                    { terminalFailure = true; continuationFailure = continuation.ErrorCode ?? "workflow_step_failed"; }
                }
            // A fresh approval child leaves the parent in WaitingForTools until it resolves.
            if (newChildProposed) { offset++; continue; }
            var partialWorkflow = !terminalFailure && skillRecords.Any(x => x.SkillId.StartsWith("workflow.", StringComparison.Ordinal)
                && x.Status == "AwaitingApproval" && !resumedWorkflow);
            parent.Status = terminalFailure || partialWorkflow ? QuacktionStatus.Failed : QuacktionStatus.Completed;
            parent.FailureCode = continuationFailure ?? (terminalFailure ? "mcp_child_operation_failed_or_rejected"
                : partialWorkflow ? "workflow_requires_continuation" : null);
            parent.ResultJson = terminalFailure ? null : System.Text.Json.JsonSerializer.Serialize(new
            {
                status = partialWorkflow ? "partially_completed" : "completed", completed = !partialWorkflow,
                operations = children.Select(x => new { taskId = x.Id, result = x.ResultJson })
            });
            if (parent.ResultJson?.Length > 524288) { parent.ResultJson = null; parent.Status = QuacktionStatus.Failed; parent.FailureCode = "agent_result_too_large"; }
            if (parent.History.LastOrDefault() is { } execution)
            { execution.FinishedAt = DateTime.UtcNow; execution.Outcome = parent.Status.ToString(); execution.FailureCode = parent.FailureCode; }
            if (await repo.ReplaceAsync(parent, ct))
            {
                if (skillExecutions is not null)
                    foreach (var skill in skillRecords)
                    {
                        if (resumedWorkflow && skill.SkillId.StartsWith("workflow.", StringComparison.Ordinal)) continue;
                        var child = children.Single(x => x.Id == skill.ApprovalTaskId);
                        skill.Status = child.Status == QuacktionStatus.Completed
                            ? skill.SkillId.StartsWith("workflow.", StringComparison.Ordinal) ? "PartiallyCompleted" : "Completed"
                            : child.Status is QuacktionStatus.Rejected or QuacktionStatus.Cancelled ? "Cancelled" : "Failed";
                        skill.ErrorCode = child.FailureCode;
                        skill.CompletedAt = DateTime.UtcNow;
                        await Guard.Save(skillExecutions, skill, ct);
                    }
                await repo.SyncAuditAsync(parent, ct);
                await events.PublishAsync(parent.OwnerAccountId, parent.SessionId, "QuacktionStatusChanged", parent, ct);
            }
        }
        }
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
