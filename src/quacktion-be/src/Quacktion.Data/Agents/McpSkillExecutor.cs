using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed class McpSkillExecutor(IMcpSkillRegistry registry, IMcpToolGateway gateway,
    IMcpSkillExecutionRepo executions, ILogger<McpSkillExecutor> logger,
    IQuacktionRepo? tasks = null, IOptions<McpSettings>? settings = null) : IMcpSkillExecutor
{
    public async Task<SkillExecutionResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken ct)
    {
        Guard.Input(request.OwnerAccountId.Length > 0 && request.SessionId.Length > 0 &&
            request.ConnectionId.Length > 0 && request.ParentTaskId.Length > 0 &&
            request.Arguments.ValueKind == JsonValueKind.Object && request.Arguments.GetRawText().Length <= 32768,
            "skill_request_invalid");
        var definitions = await registry.GetSkillsForConnectionAsync(request.OwnerAccountId, request.ConnectionId, ct);
        var skill = definitions.SingleOrDefault(x => x.Id == request.SkillId)
            ?? throw new AppException(404, "skill_not_found");
        Guard.State(skill.Available, "skill_unavailable");
        McpSkillCatalog.All.TryGetValue(skill.Id, out var template);
        Guard.State(template is not null || skill.ServerId is not null && skill.ToolName is not null, "skill_tool_missing");
        McpClientGateway.ValidateArguments(new("skill", null, skill.InputSchema, true, false), request.Arguments);
        var execution = new McpSkillExecution
        {
            Id = request.InvocationId is { Length: > 0 and <= 160 }
                ? Secrets.Digest(request.ParentTaskId, request.InvocationId + ":" + request.SkillId + ":" + request.Arguments.GetRawText())
                : Guid.NewGuid().ToString("N"),
            UserId = request.OwnerAccountId, SessionId = request.SessionId,
            ParentTaskId = request.ParentTaskId, ConnectionId = request.ConnectionId, SkillId = request.SkillId,
            Status = "Validating"
        };
        try { await executions.InsertAsync(execution, ct); }
        catch (AppException e) when (e.Code == "duplicate_key")
        {
            var prior = Guard.Found(await executions.GetAsync(execution.Id, ct));
            Guard.State(prior.UserId == request.OwnerAccountId && prior.ParentTaskId == request.ParentTaskId, "skill_execution_conflict");
            return new(prior.Status == "AwaitingApproval" ? "awaiting_approval" : "already_attempted",
                ErrorCode: prior.ErrorCode, ApprovalTaskId: prior.ApprovalTaskId, ExecutionId: prior.Id);
        }
        try
        {
            execution.Status = "Executing";
            await Guard.Save(executions, execution, ct);
            var result = template is null
                ? await ExecuteDiscoveredToolAsync(request, skill, ct)
                : template.Mode == "workflow"
                    ? await ExecuteWorkflowAsync(request, template, definitions, ct)
                    : await ExecuteToolAsync(request, template, ct);
            execution.Status = result.Status switch
            {
                "awaiting_approval" => "AwaitingApproval",
                "partially_failed" => "PartiallyCompleted",
                _ => "Completed"
            };
            execution.ApprovalTaskId = result.ApprovalTaskId;
            if (template?.Mode == "workflow" && result.Status == "awaiting_approval")
            {
                execution.NextStepIndex = result.Output!.Value.GetProperty("pendingStepIndex").GetInt32() + 1;
                execution.EncryptedPlan = Seal(request.Arguments.GetRawText(), execution.Id);
            }
            execution.ErrorCode = result.ErrorCode;
            execution.ResultSummary = result.Status switch
            {
                "awaiting_approval" => "A protected MCP operation awaits owner approval.",
                "partially_failed" => "Earlier workflow steps completed; a later step failed.",
                _ => "Skill operation completed."
            };
            execution.CompletedAt = execution.Status == "AwaitingApproval" ? null : DateTime.UtcNow;
            await Guard.Save(executions, execution, ct);
            return result with { ExecutionId = execution.Id };
        }
        catch (Exception e) when (e is AppException or HttpRequestException or TimeoutException or JsonException or InvalidOperationException)
        {
            execution.Status = "Failed";
            execution.ErrorCode = e is AppException app ? app.Code : "skill_execution_failed";
            execution.CompletedAt = DateTime.UtcNow;
            await Guard.Save(executions, execution, ct);
            logger.LogWarning("Skill execution failed {SkillId} {ExecutionId} {ErrorCode}", request.SkillId, execution.Id, execution.ErrorCode);
            return new("failed", ErrorCode: execution.ErrorCode, ExecutionId: execution.Id);
        }
    }

    private async Task<SkillExecutionResult> ExecuteDiscoveredToolAsync(SkillExecutionRequest request, McpSkillDefinition skill, CancellationToken ct)
    {
        // The gateway re-discovers the tool and validates owner, enabled services/tools, schema and approval.
        // Dynamic tools are not reviewed Google templates; every non-read operation requires exact approval.
        var result = await gateway.ExecuteOrProposeAsync(new(request.OwnerAccountId, request.SessionId,
            request.ConnectionId, skill.ToolName!, request.Arguments, skill.ServerId!, request.ParentTaskId,
            ForceApproval: skill.RequiresApproval), ct);
        if (result.TryGetProperty("status", out var status) && status.GetString() == "awaiting_approval")
            return new("awaiting_approval", ApprovalTaskId: result.GetProperty("quacktionId").GetString());
        return new("completed", result);
    }

    public async Task<SkillExecutionResult> ResumeAsync(string executionId, string userId, string parentTaskId, CancellationToken ct)
    {
        Guard.State(tasks is not null, "skill_resume_unavailable");
        var execution = Guard.Found(await executions.FindAsync(x => x.Id == executionId && x.UserId == userId
            && x.ParentTaskId == parentTaskId, ct));
        Guard.State(execution.SkillId.StartsWith("workflow.", StringComparison.Ordinal)
            && execution.Status == "AwaitingApproval" && execution.ApprovalTaskId is not null
            && execution.EncryptedPlan is not null, "skill_not_awaiting_continuation");
        var approvedStep = await tasks!.FindAsync(x => x.Id == execution.ApprovalTaskId && x.OwnerAccountId == userId
            && x.SessionId == execution.SessionId, ct);
        Guard.State(approvedStep?.Status == QuacktionStatus.Completed && QuacktionService.ValidApproval(approvedStep),
            "skill_step_not_approved");
        var plain = Open(execution.EncryptedPlan!, execution.Id);
        JsonDocument plan;
        try { plan = JsonDocument.Parse(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        using (plan)
        {
        var template = McpSkillCatalog.All[execution.SkillId];
        var definitions = await registry.GetSkillsForConnectionAsync(userId, execution.ConnectionId, ct);
        Guard.State(definitions.Any(x => x.Id == execution.SkillId && x.Available), "skill_unavailable");
        var request = new SkillExecutionRequest(userId, execution.SessionId, execution.ConnectionId, parentTaskId,
            execution.SkillId, plan.RootElement.Clone());
        var result = await ExecuteWorkflowAsync(request, template, definitions, ct, execution.NextStepIndex);
        execution.ApprovalTaskId = result.ApprovalTaskId;
        execution.ErrorCode = result.ErrorCode;
        execution.Status = result.Status switch
        {
            "awaiting_approval" => "AwaitingApproval",
            "partially_failed" => "PartiallyCompleted",
            _ => "Completed"
        };
        if (result.Status == "awaiting_approval")
            execution.NextStepIndex = result.Output!.Value.GetProperty("pendingStepIndex").GetInt32() + 1;
        else
        {
            execution.EncryptedPlan = null;
            execution.CompletedAt = DateTime.UtcNow;
        }
        execution.ResultSummary = execution.Status switch
        {
            "AwaitingApproval" => "The next protected workflow step awaits approval.",
            "PartiallyCompleted" => "A later workflow step failed after earlier steps completed.",
            _ => "All workflow steps completed."
        };
        await Guard.Save(executions, execution, ct);
        return result with { ExecutionId = execution.Id };
        }
    }

    private async Task<SkillExecutionResult> ExecuteWorkflowAsync(SkillExecutionRequest request, McpSkillTemplate template,
        IReadOnlyList<McpSkillDefinition> definitions, CancellationToken ct, int startIndex = 0)
    {
        var steps = request.Arguments.GetProperty("steps").EnumerateArray().ToArray();
        if (steps.Length != template.Steps!.Length || startIndex < 0 || startIndex > steps.Length)
            throw new AppException(400, "skill_workflow_steps_invalid");
        var completed = new List<object>();
        for (var i = startIndex; i < steps.Length; i++)
        {
            var id = steps[i].GetProperty("skillId").GetString();
            if (id != template.Steps[i] || !definitions.Any(x => x.Id == id && x.Available))
                throw new AppException(400, "skill_workflow_steps_invalid");
            var stepArgs = steps[i].GetProperty("arguments");
            var definition = definitions.Single(x => x.Id == id);
            SkillExecutionResult result;
            try
            {
                McpClientGateway.ValidateArguments(new("skill", null, definition.InputSchema, true, false), stepArgs);
                result = await ExecuteToolAsync(request with { SkillId = id!, Arguments = stepArgs }, McpSkillCatalog.All[id!], ct, i);
            }
            catch (Exception e) when (e is AppException or HttpRequestException or TimeoutException or JsonException)
            {
                var code = e is AppException app ? app.Code : "skill_step_failed";
                return new("partially_failed", JsonSerializer.SerializeToElement(new { completed, failedStep = id, errorCode = code }),
                    ErrorCode: code);
            }
            if (result.Status == "awaiting_approval")
                return new("awaiting_approval", JsonSerializer.SerializeToElement(new { completed, pendingStep = id, pendingStepIndex = i }),
                    ApprovalTaskId: result.ApprovalTaskId);
            completed.Add(new { skillId = id, output = result.Output });
        }
        return new("completed", JsonSerializer.SerializeToElement(new { completed }));
    }

    private async Task<SkillExecutionResult> ExecuteToolAsync(SkillExecutionRequest request, McpSkillTemplate template,
        CancellationToken ct, int? workflowStep = null)
    {
        var args = Transform(template, request.Arguments);
        var server = McpSkillRegistry.ToolKey(template).Split('/')[0];
        var approvalContext = template.Mode == "delete_event"
            ? "recurrenceScope=" + request.Arguments.GetProperty("targetScope").GetString()
            : template.Permission == SkillPermissionLevel.ExternalCommunication
                ? "sendingConnection=" + request.ConnectionId : null;
        if (workflowStep is { } step) approvalContext = "workflowStep=" + step +
            (approvalContext is null ? "" : ";" + approvalContext);
        var result = await gateway.ExecuteOrProposeAsync(new(request.OwnerAccountId, request.SessionId,
            request.ConnectionId, template.Tool!, JsonSerializer.SerializeToElement(args), server, request.ParentTaskId,
            template.Id, approvalContext, template.Permission == SkillPermissionLevel.Destructive), ct);
        if (result.TryGetProperty("status", out var status) && status.GetString() == "awaiting_approval")
            return new("awaiting_approval", ApprovalTaskId: result.GetProperty("quacktionId").GetString());
        return new("completed", Project(template.Id, result, request.Arguments));
    }

    private static Dictionary<string, object?> Transform(McpSkillTemplate template, JsonElement input)
    {
        var args = input.EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone(), StringComparer.Ordinal);
        switch (template.Mode)
        {
            case "create_meet": args["addMeetLink"] = true; break;
            case "add_meet": args["addMeetLink"] = true; break;
            case "restrict_access": args["accessType"] = "RESTRICTED"; break;
            case "relative_range":
            {
                var zoneName = input.GetProperty("timeZone").GetString()!;
                TimeZoneInfo zone;
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName); }
                catch (TimeZoneNotFoundException) { throw new AppException(400, "skill_time_zone_invalid"); }
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
                var start = template.Id switch
                {
                    "calendar.get_tomorrow" => today.AddDays(1),
                    "calendar.get_week" => today.AddDays(-((int)today.DayOfWeek + 6) % 7),
                    _ => today
                };
                var end = start.AddDays(template.Id == "calendar.get_week" ? 7 : 1);
                args.Remove("timeZone");
                args["timeMin"] = TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue), zone).ToString("O", CultureInfo.InvariantCulture);
                args["timeMax"] = TimeZoneInfo.ConvertTimeToUtc(end.ToDateTime(TimeOnly.MinValue), zone).ToString("O", CultureInfo.InvariantCulture);
                break;
            }
            case "upcoming": args["timeMin"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture); break;
            case "search_filter":
            {
                var query = args.TryGetValue("query", out var value) && value is JsonElement json && json.ValueKind == JsonValueKind.String
                    ? json.GetString() ?? "" : "";
                var suffix = template.Id switch
                {
                    "gmail.get_unread" => "is:unread",
                    "gmail.get_important" => "is:important",
                    "gmail.get_starred" => "is:starred",
                    "gmail.get_from_sender" => "from:" + input.GetProperty("sender").GetString(),
                    _ => ""
                };
                if (template.Id == "gmail.get_from_sender")
                {
                    var sender = input.GetProperty("sender").GetString()!;
                    if (sender.Length > 254 || sender.Any(char.IsWhiteSpace) || !sender.Contains('@'))
                        throw new AppException(400, "skill_sender_invalid");
                }
                args.Remove("sender");
                args["query"] = string.Join(' ', new[] { query, suffix }.Where(s => !string.IsNullOrWhiteSpace(s)));
                break;
            }
            case "label_change":
            {
                var label = template.Id switch
                {
                    "gmail.mark_read" or "gmail.mark_unread" => "UNREAD",
                    "gmail.star_message" or "gmail.unstar_message" => "STARRED",
                    "gmail.archive_message" or "gmail.move_to_inbox" => "INBOX",
                    _ => input.GetProperty("labelId").GetString()!
                };
                if (template.Id is "gmail.add_label" or "gmail.remove_label"
                    && (label.Length is 0 or > 128 || !label.StartsWith("Label_", StringComparison.Ordinal)))
                    throw new AppException(400, "skill_user_label_required");
                args.Remove("labelId");
                var add = template.Id is "gmail.mark_unread" or "gmail.star_message" or "gmail.add_label" or "gmail.move_to_inbox";
                args["addLabelIds"] = add ? new[] { label } : Array.Empty<string>();
                args["removeLabelIds"] = add ? Array.Empty<string>() : new[] { label };
                break;
            }
            case "delete_event":
            {
                var scope = input.GetProperty("targetScope").GetString();
                var id = input.GetProperty("eventId").GetString() ?? "";
                if (scope == "occurrence" && !System.Text.RegularExpressions.Regex.IsMatch(id, @"_[0-9]{8}T[0-9]{6}Z$"))
                    throw new AppException(400, "recurring_occurrence_id_required");
                if (scope is not ("occurrence" or "series")) throw new AppException(400, "recurrence_scope_required");
                args.Remove("targetScope");
                break;
            }
        }
        if (template.Id == "calendar.reschedule_event" && (!args.ContainsKey("start") || !args.ContainsKey("end")))
            throw new AppException(400, "skill_time_range_required");
        if (template.Id is "gmail.send_email" or "gmail.create_draft")
        {
            if (!input.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.Array || to.GetArrayLength() == 0)
                throw new AppException(400, "skill_recipients_required");
            foreach (var field in new[] { "to", "cc", "bcc" })
                if (input.TryGetProperty(field, out var addresses))
                    foreach (var address in addresses.EnumerateArray())
                    {
                        var text = address.GetString();
                        if (text is null || text.Length > 254 || text.Contains('\r') || text.Contains('\n')
                            || !System.Net.Mail.MailAddress.TryCreate(text, out var parsed) || parsed.Address != text)
                            throw new AppException(400, "skill_recipient_invalid");
                    }
            if (!input.TryGetProperty("subject", out var subject) || string.IsNullOrWhiteSpace(subject.GetString())
                || !input.TryGetProperty("body", out var body) || string.IsNullOrWhiteSpace(body.GetString()))
                throw new AppException(400, "skill_email_content_required");
        }
        if (template.Id == "meet.restrict_access") args["accessType"] = "RESTRICTED";
        if (template.Mode == "free_slots") args.Remove("durationMinutes");
        return args;
    }

    private static JsonElement Project(string id, JsonElement result, JsonElement input)
    {
        if (id == "calendar.find_free_slots") return FreeSlots(Unwrap(result), input);
        if (id == "calendar.find_conflicts") return Conflicts(Unwrap(result));
        if (id is not ("calendar.get_attendees" or "gmail.get_attachments" or "meet.get_meeting_link")) return result;
        var value = Unwrap(result);
        var property = id switch
        {
            "calendar.get_attendees" => "attendees", "gmail.get_attachments" => "attachments", _ => "meetingUri"
        };
        if (!value.TryGetProperty(property, out var projected))
        {
            if (id == "meet.get_meeting_link") throw new AppException(502, "skill_result_missing_field");
            return JsonSerializer.SerializeToElement(new Dictionary<string, object> { [property] = Array.Empty<object>() });
        }
        return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [property] = projected.Clone() });
    }

    private static JsonElement FreeSlots(JsonElement calendars, JsonElement input)
    {
        if (!DateTimeOffset.TryParse(input.GetProperty("timeMin").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var start)
            || !DateTimeOffset.TryParse(input.GetProperty("timeMax").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var end)
            || end <= start || end - start > TimeSpan.FromDays(31)) throw new AppException(400, "skill_time_range_invalid");
        var duration = input.GetProperty("durationMinutes").GetInt32();
        var busy = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var calendar in calendars.EnumerateObject())
        {
            if (calendar.Value.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new AppException(502, "calendar_availability_incomplete");
            if (!calendar.Value.TryGetProperty("busy", out var intervals)) throw new AppException(502, "calendar_availability_incomplete");
            foreach (var item in intervals.EnumerateArray())
            {
                if (!DateTimeOffset.TryParse(item.GetProperty("start").GetString(), out var from)
                    || !DateTimeOffset.TryParse(item.GetProperty("end").GetString(), out var to))
                    throw new AppException(502, "calendar_availability_invalid");
                busy.Add((from, to));
            }
        }
        if (calendars.EnumerateObject().MoveNext() == false) throw new AppException(502, "calendar_availability_incomplete");
        var slots = new List<object>();
        var cursor = start;
        foreach (var interval in busy.OrderBy(x => x.Start))
        {
            if (interval.Start - cursor >= TimeSpan.FromMinutes(duration))
                slots.Add(new { start = cursor.ToString("O"), end = interval.Start.ToString("O") });
            if (interval.End > cursor) cursor = interval.End;
        }
        if (end - cursor >= TimeSpan.FromMinutes(duration))
            slots.Add(new { start = cursor.ToString("O"), end = end.ToString("O") });
        return JsonSerializer.SerializeToElement(new { slots, minimumDurationMinutes = duration });
    }

    private static JsonElement Conflicts(JsonElement response)
    {
        if (!response.TryGetProperty("events", out var events)) throw new AppException(502, "calendar_events_invalid");
        var entries = new List<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var item in events.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.GetString() is not { } eventId
                || !item.TryGetProperty("start", out var start) || !item.TryGetProperty("end", out var end)
                || !TryEventTime(start, out var from) || !TryEventTime(end, out var to)) continue;
            entries.Add((eventId, from, to));
        }
        var conflicts = new List<object>();
        var ordered = entries.OrderBy(x => x.Start).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            for (var j = i + 1; j < ordered.Length && ordered[j].Start < ordered[i].End; j++)
                if (ordered[j].End > ordered[i].Start)
                    conflicts.Add(new { firstEventId = ordered[i].Id, secondEventId = ordered[j].Id });
        return JsonSerializer.SerializeToElement(new { conflicts,
            partial = response.TryGetProperty("nextPageToken", out var token) && !string.IsNullOrEmpty(token.GetString()) });
    }

    private static bool TryEventTime(JsonElement value, out DateTimeOffset time)
    {
        time = default;
        var text = value.TryGetProperty("dateTime", out var dateTime) ? dateTime.GetString()
            : value.TryGetProperty("date", out var date) ? date.GetString() : null;
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out time);
    }

    private static JsonElement Unwrap(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content))
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("text", out var text) && text.GetString() is { } raw)
                {
                    using var parsed = JsonDocument.Parse(raw);
                    return parsed.RootElement.Clone();
                }
        throw new AppException(502, "skill_result_invalid");
    }

    private byte[] EncryptionKey()
    {
        if (settings?.Value.CredentialEncryptionKey is not { } encoded) throw new AppException(503, "mcp_encryption_not_configured");
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length == 32) return key;
        }
        catch (FormatException) { }
        throw new AppException(503, "mcp_encryption_not_configured");
    }

    private string Seal(string value, string id)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try { using var aes = new AesGcm(EncryptionKey(), 16); aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(id)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private byte[] Open(string value, string id)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < 29) throw new AppException(409, "skill_plan_invalid");
        var plain = new byte[bytes.Length - 28];
        try
        {
            using var aes = new AesGcm(EncryptionKey(), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(id));
            return plain;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(plain); throw new AppException(409, "skill_plan_invalid"); }
    }
}
