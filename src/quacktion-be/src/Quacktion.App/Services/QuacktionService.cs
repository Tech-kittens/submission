using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using TaskAction = Quacktion.App.Models.Quacktion;
namespace Quacktion.App.Services;

public sealed class ActionRiskEvaluator : IActionRiskEvaluator
{
    // These exact capabilities must remain read-only in every registered gateway.
    private static readonly HashSet<string> Safe = ["internal.research", "internal.summary", "internal.suggestions", "internal.analysis", "claims.verify"];
    public bool IsSafe(string actionType) => Safe.Contains(actionType);
}
public sealed class QuacktionService(IQuacktionRepo repo, ISessionService sessions, IActionRiskEvaluator risk, ISessionEvents events) : IQuacktionService
{
    public async Task<TaskAction> ProposeAsync(string owner, string session, ProposalRequest request, CancellationToken ct)
    {
        var s = await sessions.GetAsync(owner, session, ct);
        Guard.State(s.Status is SessionStatus.Active or SessionStatus.Completed, "session_not_ready");
        try { using var json = JsonDocument.Parse(request.ParametersJson); Guard.Input(json.RootElement.ValueKind == JsonValueKind.Object); }
        catch (JsonException) { throw new AppException(400, "invalid_parameters_json"); }
        Guard.Input(request.ParametersJson.Length <= 32768 && request.ActionType.Length <= 128);
        var action = new TaskAction
        {
            OwnerAccountId = owner,
            SessionId = session,
            ActionType = request.ActionType,
            ParametersJson = request.ParametersJson,
            ActionDigest = Secrets.Digest(request.ActionType, request.ParametersJson),
            Status = risk.IsSafe(request.ActionType) ? QuacktionStatus.Queued : QuacktionStatus.PendingApproval
        };
        if (action.Status == QuacktionStatus.PendingApproval) action.Approval = new() { QuacktionId = action.Id, OwnerAccountId = owner, ActionDigest = action.ActionDigest };
        await repo.InsertAsync(action, ct);
        await events.PublishAsync(owner, session, "QuacktionProposed", action, ct);
        if (action.Approval is not null) await events.PublishAsync(owner, session, "ApprovalRequested", action.Approval, ct);
        return action;
    }
    public async Task<TaskAction> GetAsync(string owner, string id, CancellationToken ct) => Guard.Found(await repo.FindAsync(x => x.Id == id && x.OwnerAccountId == owner, ct));
    public async Task<IReadOnlyList<TaskAction>> ListAsync(string owner, string? session, int offset, int limit, CancellationToken ct)
    { Guard.Page(offset, limit); return await repo.ListAsync(x => x.OwnerAccountId == owner && (session == null || x.SessionId == session), offset, limit, ct); }
    public async Task<TaskAction> CancelAsync(string owner, string id, CancellationToken ct)
    {
        var q = await GetAsync(owner, id, ct); Guard.State(q.Status is QuacktionStatus.PendingApproval or QuacktionStatus.Queued or QuacktionStatus.InProgress);
        q.CancellationRequested = true;
        if (q.Status != QuacktionStatus.InProgress) q.Status = QuacktionStatus.Cancelled;
        await Guard.Save(repo, q, ct); await Publish(q, ct); return q;
    }
    public async Task<TaskAction> RetryAsync(string owner, string id, CancellationToken ct)
    {
        var q = await GetAsync(owner, id, ct); Guard.State(q.Status == QuacktionStatus.Failed && q.Attempts < 5, "retry_not_allowed");
        Guard.State(risk.IsSafe(q.ActionType) || ValidApproval(q), "approval_required");
        q.Status = QuacktionStatus.Queued; q.NextAttemptAt = DateTime.UtcNow; q.FailureCode = null;
        await Guard.Save(repo, q, ct); await Publish(q, ct); return q;
    }
    internal static bool ValidApproval(TaskAction q) => q.Approval is { Status: ApprovalStatus.Approved } a && a.OwnerAccountId == q.OwnerAccountId && a.QuacktionId == q.Id && a.ActionDigest == Secrets.Digest(q.ActionType, q.ParametersJson);
    private Task Publish(TaskAction q, CancellationToken ct) => events.PublishAsync(q.OwnerAccountId, q.SessionId, "QuacktionStatusChanged", q, ct);
}
public sealed class ApprovalService(IQuacktionService actions, IQuacktionRepo repo, ISessionEvents events, ILogger<ApprovalService> logger) : IApprovalService
{
    public async Task<TaskAction> ResolveAsync(string owner, string id, string digest, bool approve, CancellationToken ct)
    {
        var q = await actions.GetAsync(owner, id, ct);
        Guard.State(q.Status == QuacktionStatus.PendingApproval && q.Approval?.Status == ApprovalStatus.Pending);
        Guard.State(q.Approval!.OwnerAccountId == owner && q.Approval.ActionDigest == digest && digest == Secrets.Digest(q.ActionType, q.ParametersJson), "action_changed");
        q.Approval.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected; q.Approval.ResolvedAt = DateTime.UtcNow;
        q.Status = approve ? QuacktionStatus.Queued : QuacktionStatus.Rejected;
        await Guard.Save(repo, q, ct);
        logger.LogInformation("ApprovalResolved {AccountId} {QuacktionId} {Approved} {ActionDigest}", owner, id, approve, digest);
        await events.PublishAsync(owner, q.SessionId, "ApprovalResolved", q.Approval, ct);
        await events.PublishAsync(owner, q.SessionId, "QuacktionStatusChanged", q, ct); return q;
    }
}
