using System.Text.Json;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;

namespace Quacktion.App.Services;

public sealed class MeetingToolService(IMeetingToolCallRepo calls, IQuacktionService actions,
    ISessionRepo sessions, IAccountRepo accounts, MeetingService? meetingLifecycle = null,
    IDeviceRepo? devices = null, IMeetingRepo? meetings = null,
    IMcpConnectionService? mcpConnections = null, IBriefingConversation? briefings = null,
    IApprovalService? approvals = null)
{
public static readonly string[] Capabilities = ["internal.preparation", "internal.summary", "internal.analysis", "internal.research", "internal.suggestions", "internal.mcp_read", "mcp.request"];

    public async Task<MeetingToolCall> AcceptAsync(string owner, string session, string callId, string name, JsonElement args, CancellationToken ct)
    {
        await Authorize(owner, session, ct);
        Guard.Input(callId.Length is > 0 and <= 200 && name.Length is > 0 and <= 128, "invalid_tool_call");
        var raw = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
        Guard.Input(raw.Length <= 32768, "tool_arguments_too_large");
        var id = Secrets.Digest(owner, session + ":" + callId);
        var existing = await calls.GetAsync(id, ct);
        if (existing is not null)
        {
            Guard.State(existing.Name == name && existing.ArgumentsJson == raw, "tool_call_conflict");
            return existing;
        }
        var call = new MeetingToolCall { Id = id, OwnerAccountId = owner, SessionId = session, CallId = callId, Name = name, ArgumentsJson = raw };
        try { await calls.InsertAsync(call, ct); }
        catch (AppException e) when (e.Code == "duplicate_key")
        { return await AcceptAsync(owner, session, callId, name, args, ct); }
        return call;
    }

    // Each tick does bounded database work. Long-running reasoning belongs to the durable execution worker.
    public async Task<MeetingToolCall> ResolveAsync(MeetingToolCall call, CancellationToken ct)
    {
        await Authorize(call.OwnerAccountId, call.SessionId, ct);
        if (call.Cancelled || call.ResultJson is not null) return call;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var args = document.RootElement;
            Guard.Input(args.ValueKind == JsonValueKind.Object, "invalid_tool_arguments");
            if (call.Name == "meeting_briefing")
            {
                Guard.State(briefings is not null, "meeting_preparation_unavailable");
                Fields(args, "operation", "meetingId", "query");
                call.ResultJson = await briefings!.ExecuteAsync(call.OwnerAccountId, call.SessionId, Required(args, "operation", 30),
                    args.TryGetProperty("meetingId", out var meetingId) ? meetingId.GetString() : null, ct,
                    args.TryGetProperty("query", out var query) ? query.GetString() : null);
                // Results remain JSON for the existing Live response dispatcher.
                try { using var parsed = JsonDocument.Parse(call.ResultJson); }
                catch (JsonException) { call.ResultJson = JsonSerializer.Serialize(new { message = call.ResultJson }); }
            }
            else if (call.Name == "get_quacktion_status")
            {
                Fields(args, "quacktionId");
                var q = await OwnedAction(call, Required(args, "quacktionId", 128), ct);
                call.ResultJson = JsonSerializer.Serialize(new { quacktionId = q.Id, status = q.Status.ToString(), q.FailureCode });
            }
            else if (call.Name == "resolve_voice_approval")
            {
                Guard.State(approvals is not null, "voice_approval_unavailable");
                Fields(args, "quacktionId", "approve");
                Guard.Input(args.TryGetProperty("approve", out var decision) && decision.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "invalid_tool_arguments");
                var q = await OwnedAction(call, Required(args, "quacktionId", 128), ct);
                Guard.State(q.Status == QuacktionStatus.PendingApproval && q.Approval is { Status: ApprovalStatus.Pending },
                    "approval_not_pending");
                var resolved = await approvals!.ResolveAsync(call.OwnerAccountId, q.Id, q.Approval!.ActionDigest,
                    decision.GetBoolean(), ct);
                call.ResultJson = JsonSerializer.Serialize(new { quacktionId = resolved.Id, status = resolved.Status.ToString(),
                    approvalStatus = resolved.Approval?.Status.ToString(), resolved.FailureCode });
            }
            else if (call.Name == "start_meeting")
            {
                Guard.State(meetingLifecycle is not null, "meeting_mode_unavailable");
                Fields(args, "recordingConsent", "name");
                Guard.Input(args.TryGetProperty("recordingConsent", out var consent) && consent.ValueKind == JsonValueKind.True,
                    "recording_consent_required");
                var session = Guard.Found(await sessions.GetAsync(call.SessionId, ct));
                Guard.State(session.DeviceId is not null, "device_required");
                var name = args.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                    ? nameNode.GetString() ?? "Meeting" : "Meeting";
                var meeting = await meetingLifecycle!.StartAsync(call.OwnerAccountId, session.DeviceId!, name, ct,
                    recordingConsent: true);
                call.ResultJson = JsonSerializer.Serialize(new { meetingId = meeting.Id, status = "Active" });
            }
            else if (call.Name == "end_meeting")
            {
                Guard.State(meetingLifecycle is not null && devices is not null && meetings is not null, "meeting_mode_unavailable");
                Fields(args);
                var session = Guard.Found(await sessions.GetAsync(call.SessionId, ct));
                Guard.State(session.DeviceId is not null, "device_required");
                var device = Guard.Found(await devices!.GetAsync(session.DeviceId!, ct));
                Guard.State(device.MeetingId is not null, "meeting_not_started");
                var meeting = Guard.Found(await meetings!.GetAsync(device.MeetingId!, ct));
                Guard.State(meeting.OwnerAccountId == call.OwnerAccountId, "meeting_owner_mismatch");
                meeting = await meetingLifecycle!.EndAsync(call.OwnerAccountId, meeting.Id, ct);
                call.ResponseScheduling = "SILENT";
                call.ResultJson = JsonSerializer.Serialize(new { meetingId = meeting.Id, status = meeting.Status.ToString() });
            }
            else
            {
                if (call.QuacktionId is null)
                {
                    ProposalRequest proposal;
                    if (call.Name == "execute_quacktion")
                    {
                        Fields(args, "request", "announcementPolicy");
                        var announcementPolicy = args.TryGetProperty("announcementPolicy", out var preference) ? preference.GetString() : "ANNOUNCE_WHEN_IDLE";
                        Guard.Input(announcementPolicy is "ANNOUNCE_WHEN_IDLE" or "ANNOUNCE_IMMEDIATELY" or "PRIVATE_NOTIFICATION" or "APPROVAL_REQUIRED", "invalid_announcement_policy");
                        proposal = new("mcp.request", JsonSerializer.Serialize(new { query = Required(args, "request", 12000), announcementPolicy }), "gemini:" + call.Id);
                    }
                    else if (call.Name == "create_quacktion" || call.Name == "research_external_information")
                    {
                        Fields(args, call.Name == "create_quacktion" ? ["actionType", "query"] : ["query"]);
                        var capability = call.Name == "create_quacktion" ? Required(args, "actionType", 128) : "internal.research";
                        Guard.Input(Capabilities.Contains(capability), "unsupported_tool_capability");
                        var query = Required(args, "query", 12000);
                        proposal = new(capability, JsonSerializer.Serialize(new { query }), "gemini:" + call.Id);
                    }
                    else if (call.Name is "announce_quacktion_result" or "request_quacktion_approval")
                    {
                        Fields(args, "quacktionId");
                        var source = await OwnedAction(call, Required(args, "quacktionId", 128), ct);
                        Guard.State(source.Status == QuacktionStatus.Completed && (Capabilities.Contains(source.ActionType) || source.ActionType == "mcp.invoke"), "result_not_ready");
                        Guard.Input(source.ResultJson is { Length: > 0 and <= 28000 }, "result_too_large_to_announce");
                        // Snapshot the exact content into the approval digest. No result reaches Live before approval.
                        proposal = new("meeting.announce", JsonSerializer.Serialize(new { sourceQuacktionId = source.Id, content = source.ResultJson }), "announce:" + source.Id);
                    }
                    else throw new AppException(400, "unknown_tool");
                    Models.Quacktion created;
                    if (proposal.ActionType is "mcp.request" or "internal.mcp_read" && mcpConnections is not null)
                    {
                        using var proposalParameters = JsonDocument.Parse(proposal.ParametersJson);
                        var requestText = proposalParameters.RootElement.TryGetProperty("query", out var queryNode)
                            ? queryNode.GetString() ?? "" : "";
                        var githubRequest = requestText.Contains("github", StringComparison.OrdinalIgnoreCase)
                            || requestText.Contains("repository", StringComparison.OrdinalIgnoreCase)
                            || requestText.Contains("pull request", StringComparison.OrdinalIgnoreCase);
                        var candidates = (await mcpConnections.ListAsync(call.OwnerAccountId, ct))
                            .Where(x => x.Status == "Connected" && x.EnabledTools.Count > 0
                                && x.EnabledServices is { Count: > 0 })
                            .Select(x => new
                            {
                                Connection = x,
                                Services = githubRequest
                                    ? x.EnabledServices!.Where(s => string.Equals(s, "github:github", StringComparison.OrdinalIgnoreCase)).ToArray()
                                    : x.EnabledServices!.ToArray()
                            })
                            .Where(x => x.Services.Length > 0).ToArray();
                        created = candidates.Length == 1
                            ? await actions.ProposeAuthorizedMcpAsync(call.OwnerAccountId, call.SessionId, proposal,
                                candidates[0].Connection.Id, candidates[0].Services, ct)
                            : await actions.ProposeAsync(call.OwnerAccountId, call.SessionId, proposal, ct);
                    }
                    else created = await actions.ProposeAsync(call.OwnerAccountId, call.SessionId, proposal, ct);
                    call.QuacktionId = created.Id;
                }
                var task = await OwnedAction(call, call.QuacktionId, ct);
                call.ProgressStatus = task.Status == QuacktionStatus.PendingApproval ? "awaiting_authenticated_confirmation" : task.Status.ToString();
                if (task.AnnouncementPolicy == "PRIVATE_NOTIFICATION") call.ResponseScheduling = "SILENT";
                if (call.Name == "execute_quacktion" && task.Status == QuacktionStatus.Completed && task.AnnouncementPolicy != "PRIVATE_NOTIFICATION")
                {
                    Guard.Input(task.ResultJson is { Length: > 0 and <= 28000 }, "result_too_large_to_announce");
                    if (task.AutoAuthorizedMcp && task.AnnouncementPolicy != "APPROVAL_REQUIRED")
                    {
                        call.ResponseScheduling = task.AnnouncementPolicy == "ANNOUNCE_IMMEDIATELY" ? "INTERRUPT" : "WHEN_IDLE";
                        call.ResultJson = JsonSerializer.Serialize(new
                        { quacktionId = task.Id, status = "Completed", approvedAnnouncement = task.ResultJson });
                        await Guard.Save(calls, call, ct);
                        return call;
                    }
                    var announcement = await actions.ProposeAsync(call.OwnerAccountId, call.SessionId,
                        new("meeting.announce", JsonSerializer.Serialize(new { sourceQuacktionId = task.Id, content = task.ResultJson, announcementPolicy = task.AnnouncementPolicy }), "announce:" + task.Id), ct);
                    call.AnnouncementQuacktionId = announcement.Id;
                    if (announcement.Status == QuacktionStatus.Completed && QuacktionService.ValidApproval(announcement))
                    {
                        call.ResponseScheduling = task.AnnouncementPolicy == "ANNOUNCE_IMMEDIATELY" ? "INTERRUPT" : "WHEN_IDLE";
                        call.ResultJson = JsonSerializer.Serialize(new { quacktionId = task.Id, status = "Completed", approvedAnnouncement = announcement.ResultJson });
                    }
                    else if (announcement.Status is QuacktionStatus.Rejected or QuacktionStatus.Cancelled or QuacktionStatus.Failed)
                        call.ResultJson = JsonSerializer.Serialize(new { quacktionId = task.Id, status = "announcement_not_authorized", announcement.FailureCode });
                    await Guard.Save(calls, call, ct);
                    return call;
                }
                if (task.Status is QuacktionStatus.Completed or QuacktionStatus.Failed or QuacktionStatus.Cancelled or QuacktionStatus.Rejected)
                {
                    var approved = task.ActionType == "meeting.announce" && task.Status == QuacktionStatus.Completed && QuacktionService.ValidApproval(task);
                    call.ResultJson = JsonSerializer.Serialize(new
                    {
                        quacktionId = task.Id, status = task.Status.ToString(), task.FailureCode,
                        approvalRequiredToAnnounce = task.Status == QuacktionStatus.Completed && task.ActionType != "meeting.announce",
                        approvedAnnouncement = approved ? task.ResultJson : null
                    });
                }
            }
        }
        catch (Exception e) when (e is AppException or JsonException)
        { call.ResultJson = JsonSerializer.Serialize(new { error = e is AppException app ? app.Code : "invalid_tool_arguments" }); }
        await Guard.Save(calls, call, ct);
        return call;
    }

    private async Task Authorize(string owner, string sessionId, CancellationToken ct)
    {
        var account = await accounts.GetAsync(owner, ct);
        Guard.State(account is { IsActive: true }, "account_inactive");
        var session = Guard.Found(await sessions.GetAsync(sessionId, ct));
        if (session.OwnerAccountId != owner) throw new AppException(404, "not_found");
        Guard.State(session.Status == SessionStatus.Active, "session_not_active");
    }
    private async Task<Models.Quacktion> OwnedAction(MeetingToolCall call, string id, CancellationToken ct)
    {
        var q = await actions.GetAsync(call.OwnerAccountId, id, ct);
        if (q.SessionId != call.SessionId) throw new AppException(404, "not_found");
        return q;
    }
    private static string Required(JsonElement args, string name, int max)
    {
        Guard.Input(args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String, "invalid_tool_arguments");
        var text = value.GetString();
        Guard.Input(!string.IsNullOrWhiteSpace(text) && text.Length <= max, "invalid_tool_arguments");
        return text!;
    }
    private static void Fields(JsonElement args, params string[] allowed) =>
        Guard.Input(args.EnumerateObject().All(x => allowed.Contains(x.Name)), "invalid_tool_arguments");
}
