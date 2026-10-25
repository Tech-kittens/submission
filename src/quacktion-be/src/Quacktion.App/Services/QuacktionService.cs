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
    private static readonly HashSet<string> Safe = ["internal.preparation", "internal.research", "internal.summary", "internal.suggestions", "internal.analysis", "claims.verify"];
    public bool IsSafe(string actionType) => Safe.Contains(actionType);
}
public sealed class QuacktionService(IQuacktionRepo repo, ISessionService sessions, IActionRiskEvaluator risk, ISessionEvents events) : IQuacktionService
{
    public Task<TaskAction> ProposeAsync(string owner, string session, ProposalRequest request, CancellationToken ct) =>
        ProposeCoreAsync(owner, session, request, null, null, ct);

    public Task<TaskAction> ProposeAuthorizedMcpAsync(string owner, string session, ProposalRequest request,
        string connectionId, string[] services, CancellationToken ct)
    {
        Guard.Input(request.ActionType is "mcp.request" or "internal.mcp_read" && !string.IsNullOrWhiteSpace(connectionId)
            && services.Length is > 0 and <= 10 && services.Distinct(StringComparer.Ordinal).Count() == services.Length,
            "mcp_account_selection_invalid");
        return ProposeCoreAsync(owner, session, request, connectionId, services, ct);
    }

    private async Task<TaskAction> ProposeCoreAsync(string owner, string session, ProposalRequest request,
        string? connectionId, string[]? services, CancellationToken ct)
    {
        var s = await sessions.GetAsync(owner, session, ct);
        Guard.State(s.Status is SessionStatus.Active or SessionStatus.Suspended or SessionStatus.Completed, "session_not_ready");
        try { using var json = JsonDocument.Parse(request.ParametersJson); Guard.Input(json.RootElement.ValueKind == JsonValueKind.Object); }
        catch (JsonException) { throw new AppException(400, "invalid_parameters_json"); }
        Guard.Input(request.ParametersJson.Length <= 32768 && request.ActionType.Length <= 128);
        if (connectionId is not null)
        {
            using var source = JsonDocument.Parse(request.ParametersJson);
            Guard.Input(source.RootElement.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String
                && source.RootElement.EnumerateObject().All(x => x.Name is "query" or "announcementPolicy"), "mcp_request_invalid");
            var policy = source.RootElement.TryGetProperty("announcementPolicy", out var value) ? value.GetString() : "APPROVAL_REQUIRED";
            request = request with { ParametersJson = JsonSerializer.Serialize(new
                { query = query.GetString(), announcementPolicy = policy, connectionId, services }) };
        }
        var action = new TaskAction
        {
            Id = request.IdempotencyKey is null ? Guid.NewGuid().ToString("N") : Secrets.Digest(owner, session + ":" + request.IdempotencyKey),
            OwnerAccountId = owner,
            SessionId = session,
            ActionType = request.ActionType,
            ParametersJson = request.ParametersJson,
            ActionDigest = Secrets.Digest(request.ActionType, request.ParametersJson),
            Status = connectionId is not null || risk.IsSafe(request.ActionType) ? QuacktionStatus.Queued : QuacktionStatus.PendingApproval,
            AutoAuthorizedMcp = connectionId is not null
        };
        using (var parameters = JsonDocument.Parse(request.ParametersJson))
        {
            if (parameters.RootElement.TryGetProperty("announcementPolicy", out var policy))
            {
                var value = policy.GetString();
                Guard.Input(value is "ANNOUNCE_WHEN_IDLE" or "ANNOUNCE_IMMEDIATELY" or "PRIVATE_NOTIFICATION" or "APPROVAL_REQUIRED", "invalid_announcement_policy");
                action.AnnouncementPolicy = value!;
            }
        }
        if (action.Status == QuacktionStatus.PendingApproval) action.Approval = new() { QuacktionId = action.Id, OwnerAccountId = owner, ActionDigest = action.ActionDigest, ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
        if (request.IdempotencyKey is not null)
        {
            Guard.Input(request.IdempotencyKey.Length is > 0 and <= 200, "invalid_idempotency_key");
            var existing = await repo.GetAsync(action.Id, ct);
            if (existing is not null) return Match(existing, action);
        }
        try { await repo.InsertAsync(action, ct); }
        catch (AppException e) when (e.Code == "duplicate_key" && request.IdempotencyKey is not null)
        { return Match(Guard.Found(await repo.GetAsync(action.Id, ct)), action); }
        await events.PublishAsync(owner, session, "QuacktionProposed", action, ct);
        if (action.Approval is not null) await events.PublishAsync(owner, session, "ApprovalRequested", action.Approval, ct);
        return action;
    }
    public async Task<TaskAction> GetAsync(string owner, string id, CancellationToken ct) => Guard.Found(await repo.FindAsync(x => x.Id == id && x.OwnerAccountId == owner, ct));
    public async Task<IReadOnlyList<TaskAction>> ListAsync(string owner, string? session, int offset, int limit, CancellationToken ct)
    { Guard.Page(offset, limit); return await repo.ListAsync(x => x.OwnerAccountId == owner && (session == null || x.SessionId == session), offset, limit, ct); }
    public async Task<TaskAction> CancelAsync(string owner, string id, CancellationToken ct)
    {
        var q = await GetAsync(owner, id, ct); Guard.State(q.Status is QuacktionStatus.PendingApproval or QuacktionStatus.Queued or QuacktionStatus.InProgress or QuacktionStatus.WaitingForTools);
        q.CancellationRequested = true;
        if (q.Status != QuacktionStatus.InProgress) q.Status = QuacktionStatus.Cancelled;
        await Guard.Save(repo, q, ct); await Publish(q, ct); return q;
    }
    public async Task<TaskAction> RetryAsync(string owner, string id, CancellationToken ct)
    {
        var q = await GetAsync(owner, id, ct); Guard.State(q.ActionType != "mcp.invoke" && q.ChildTaskIds.Count == 0
            && q.Status == QuacktionStatus.Failed && q.Attempts < 5, "retry_not_allowed");
        Guard.State(risk.IsSafe(q.ActionType) || AuthorizedMcp(q) || ValidApproval(q), "approval_required");
        q.Status = QuacktionStatus.Queued; q.NextAttemptAt = DateTime.UtcNow; q.FailureCode = null;
        await Guard.Save(repo, q, ct); await Publish(q, ct); return q;
    }
    private static TaskAction Match(TaskAction existing, TaskAction proposed)
    {
        Guard.State(existing.OwnerAccountId == proposed.OwnerAccountId && existing.SessionId == proposed.SessionId
            && existing.ActionDigest == proposed.ActionDigest, "idempotency_key_conflict");
        return existing;
    }
    public static bool ValidApproval(TaskAction q) => q.Approval is { Status: ApprovalStatus.Approved } a
        && (a.ExpiresAt is null || a.ExpiresAt > DateTime.UtcNow)
        && a.OwnerAccountId == q.OwnerAccountId && a.QuacktionId == q.Id && a.ActionDigest == Secrets.Digest(q.ActionType, q.ParametersJson);
    public static bool AuthorizedMcp(TaskAction q) => (q.ActionType is "mcp.request" or "internal.mcp_read") && q.AutoAuthorizedMcp
        && q.Approval is null && q.ActionDigest == Secrets.Digest(q.ActionType, q.ParametersJson);
    private Task Publish(TaskAction q, CancellationToken ct) => events.PublishAsync(q.OwnerAccountId, q.SessionId, "QuacktionStatusChanged", q, ct);
}
public sealed class ApprovalService(IQuacktionService actions, IQuacktionRepo repo, ISessionEvents events, ILogger<ApprovalService> logger) : IApprovalService
{
    public async Task<TaskAction> ResolveAsync(string owner, string id, string digest, bool approve, CancellationToken ct)
    {
        var q = await actions.GetAsync(owner, id, ct);
        if (approve && QuacktionService.AuthorizedMcp(q) && q.ActionDigest == digest
            && q.Status is QuacktionStatus.Queued or QuacktionStatus.InProgress or QuacktionStatus.WaitingForTools or QuacktionStatus.Completed)
            return q; // Older app versions may still submit approval after account selection.
        if (q.Status == QuacktionStatus.PendingApproval && q.Approval is { Status: ApprovalStatus.Pending, ExpiresAt: { } expires } && expires <= DateTime.UtcNow)
        {
            q.Approval.Status = ApprovalStatus.Expired;
            q.Approval.ResolvedAt = DateTime.UtcNow;
            q.Status = QuacktionStatus.Rejected;
            q.FailureCode = "approval_expired";
            await Guard.Save(repo, q, ct);
            await events.PublishAsync(owner, q.SessionId, "ApprovalExpired", q.Approval, ct);
            await events.PublishAsync(owner, q.SessionId, "QuacktionStatusChanged", q, ct);
            throw new AppException(409, "approval_expired");
        }
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
