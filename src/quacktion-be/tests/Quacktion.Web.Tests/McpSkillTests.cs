using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.App.Tests;
using Quacktion.Data.Agents;
using Xunit;

namespace Quacktion.Web.Tests;

public sealed class McpSkillTests
{
    private sealed class Executions : MemoryRepo<McpSkillExecution>, IMcpSkillExecutionRepo;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitHubDiscoveryAndExecutionRespectToolEnablementAndApproval(bool readOnly)
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["github:github"] = [Tool("repository_operation", readOnly,
            """{"type":"object","properties":{"owner":{"type":"string"}},"required":["owner"],"additionalProperties":false}""")] };
        var connection = await f.Connect("github");
        Assert.StartsWith("github connection", connection.DisplayName);
        var registry = new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance);
        Assert.Empty(await registry.GetSkillsForConnectionAsync(f.Owner, connection.Id, default));
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["github:github/repository_operation"], default);
        var skill = Assert.Single(await registry.GetSkillsForConnectionAsync(f.Owner, connection.Id, default));
        Assert.Equal("github", skill.Provider);
        Assert.Equal("github:github", skill.ServerId);
        Assert.Equal(!readOnly, skill.RequiresApproval);
        Assert.Contains(await registry.GetAvailableSkillsAsync(f.Owner, default), x => x.Id == skill.Id && x.Available);
        var parent = await f.ConfirmedParent(connection.Id, "github:github");
        var executor = new McpSkillExecutor(registry, f.Service, new Executions(), NullLogger<McpSkillExecutor>.Instance);
        var request = new SkillExecutionRequest(f.Owner, f.Session, connection.Id, parent.Id, skill.Id,
            JsonSerializer.SerializeToElement(new { owner = "example" }), "github-call");
        var result = await executor.ExecuteAsync(request, default);
        Assert.Equal(readOnly ? "completed" : "awaiting_approval", result.Status);
        Assert.Equal(readOnly ? 1 : 0, f.Mcp.Calls);
        if (!readOnly) Assert.NotNull(result.ApprovalTaskId);
        await Assert.ThrowsAsync<AppException>(() => executor.ExecuteAsync(request with { OwnerAccountId = "other" }, default));
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, [], default);
        Assert.Empty(await registry.GetSkillsForConnectionAsync(f.Owner, connection.Id, default));
    }

    [Fact]
    public async Task VoiceCalendarRequestUsesOnlyEligibleConnectedAccountWithoutApproval()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] = [Tool("calendar_list_events", false,
            """{"type":"object","properties":{},"additionalProperties":false}""")] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:calendar/calendar_list_events"], default);
        var calls = new CallRepo();
        var voice = new MeetingToolService(calls, f.Core.ActionService, f.Core.Sessions, f.Core.Accounts,
            mcpConnections: f.Service);
        var call = await voice.AcceptAsync(f.Owner, f.Session, "voice-calendar", "execute_quacktion",
            JsonSerializer.SerializeToElement(new { request = "What's on my calendar tomorrow?" }), default);
        call = await voice.ResolveAsync(call, default);
        var parent = (await f.Core.Actions.GetAsync(call.QuacktionId!, default))!;
        Assert.Equal(QuacktionStatus.Queued, parent.Status);
        Assert.True(parent.AutoAuthorizedMcp);
        Assert.Null(parent.Approval);
        Assert.Contains("tomorrow", parent.ParametersJson);
        using (var selected = JsonDocument.Parse(parent.ParametersJson))
            Assert.Contains(selected.RootElement.GetProperty("services").EnumerateArray(),
                x => x.GetString() == "google:calendar");
        var claimed = (await f.Core.Actions.ClaimAsync(DateTime.UtcNow, TimeSpan.FromMinutes(1), default))!;
        var result = await f.Service.ExecuteOrProposeAsync(new(f.Owner, f.Session, connection.Id,
            "calendar_list_events", JsonSerializer.SerializeToElement(new { }), "google:calendar", claimed.Id,
            "calendar.list_events"), default);
        Assert.True(result.TryGetProperty("confirmed", out _));
        Assert.Equal(1, f.Mcp.Calls);
        claimed.Status = QuacktionStatus.Completed;
        claimed.ResultJson = "{\"summary\":\"Tomorrow has no events\"}";
        Assert.True(await f.Core.Actions.ReplaceAsync(claimed, default));
        call = await voice.ResolveAsync(call, default);
        Assert.Contains("Tomorrow has no events", call.ResultJson);
        Assert.Null(call.AnnouncementQuacktionId);
    }

    [Fact]
    public async Task VoiceRequestWithMultipleAccountsWaitsForAuthenticatedSelection()
    {
        var f = await McpConnectionTests.Harness.Create();
        var first = await f.ConnectAndEnable();
        _ = await f.ConnectAndEnable();
        var voice = new MeetingToolService(new CallRepo(), f.Core.ActionService, f.Core.Sessions,
            f.Core.Accounts, mcpConnections: f.Service);
        var call = await voice.AcceptAsync(f.Owner, f.Session, "choose-account", "execute_quacktion",
            JsonSerializer.SerializeToElement(new { request = "Check my calendar" }), default);
        call = await voice.ResolveAsync(call, default);
        var task = (await f.Core.Actions.GetAsync(call.QuacktionId!, default))!;
        Assert.Equal(QuacktionStatus.PendingApproval, task.Status);
        Assert.False(task.AutoAuthorizedMcp);
        task = await f.Service.BindTaskAsync(f.Owner, first.Id, task.Id, ["google:calendar"], default);
        Assert.Equal(QuacktionStatus.Queued, task.Status);
        Assert.True(task.AutoAuthorizedMcp);
        Assert.Null(task.Approval);
    }

    [Fact]
    public async Task RegistryRequiresDiscoveredAndEnabledToolAndReportsUnsupportedSkills()
    {
        var f = await McpConnectionTests.Harness.Create();
        var registry = new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance);
        var noConnection = await registry.GetAvailableSkillsAsync(f.Owner, default);
        Assert.False(noConnection.Single(x => x.Id == "calendar.list_events").Available);
        Assert.False(noConnection.Single(x => x.Id == "meet.remove_participant").Available);
        var connection = await f.Connect();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] = [Tool("calendar_list_events", true,
            """{"type":"object","properties":{"calendarId":{"type":"string"},"timeMin":{"type":"string"},"timeMax":{"type":"string"},"maxResults":{"type":"integer"}},"additionalProperties":false}""")] };
        var disabled = await registry.GetSkillsForConnectionAsync(f.Owner, connection.Id, default);
        Assert.False(disabled.Single(x => x.Id == "calendar.get_today").Available);
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:calendar/calendar_list_events"], default);
        var enabled = await registry.GetSkillsForConnectionAsync(f.Owner, connection.Id, default);
        var today = enabled.Single(x => x.Id == "calendar.get_today");
        Assert.True(today.Available);
        Assert.True(today.RequiresApproval == false); // The mock explicitly declares a trusted read tool.
        Assert.False(enabled.Single(x => x.Id == "calendar.get_event").Available);
        Assert.False(enabled.Single(x => x.Id == "gmail.delete_message").Available);
        Assert.Equal("calendar_list_events", today.RequiredCapabilities.Single());
    }

    [Fact]
    public async Task GeneralCalendarUpdateCannotReplaceAttendees()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] = [Tool("calendar_update_event", false,
            """{"type":"object","properties":{"eventId":{"type":"string"},"summary":{"type":"string"},"attendees":{"type":"array","items":{"type":"string"}}},"required":["eventId"],"additionalProperties":false}""")] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:calendar/calendar_update_event"], default);
        var registry = new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance);
        var update = (await registry.GetSkillsForConnectionAsync(f.Owner, connection.Id, default))
            .Single(x => x.Id == "calendar.update_event");
        Assert.True(update.Available);
        Assert.False(update.InputSchema.GetProperty("properties").TryGetProperty("attendees", out _));
    }

    [Fact]
    public async Task NonDestructiveLabelChangeExecutesWithoutAnotherApproval()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:gmail"] = [Tool("gmail_modify_labels", false,
            """{"type":"object","properties":{"id":{"type":"string"},"addLabelIds":{"type":"array","items":{"type":"string"}},"removeLabelIds":{"type":"array","items":{"type":"string"}}},"required":["id"],"additionalProperties":false}""")] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:gmail/gmail_modify_labels"], default);
        var parent = await f.ConfirmedParent(connection.Id, "google:gmail");
        var registry = new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance);
        var saved = new Executions();
        var executor = new McpSkillExecutor(registry, f.Service, saved, NullLogger<McpSkillExecutor>.Instance);
        var request = new SkillExecutionRequest(f.Owner, f.Session, connection.Id, parent.Id, "gmail.mark_read",
            JsonSerializer.SerializeToElement(new { id = "msg_1" }), "call-1");
        var proposed = await executor.ExecuteAsync(request, default);
        Assert.Equal("completed", proposed.Status);
        Assert.Equal(1, f.Mcp.Calls);
        Assert.Null(proposed.ApprovalTaskId);
        Assert.Equal("Completed", (await saved.GetAsync(proposed.ExecutionId!, default))!.Status);
        var replay = await executor.ExecuteAsync(request, default);
        Assert.Equal("already_attempted", replay.Status);
        Assert.Equal(1, f.Mcp.Calls);
        await Assert.ThrowsAsync<AppException>(() => executor.ExecuteAsync(request with { OwnerAccountId = "other" }, default));
    }

    [Fact]
    public async Task ReadOnlyWorkflowRunsOrderedStepsThroughGateway()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] =
        [
            Tool("calendar_list_events", true, """{"type":"object","properties":{"timeMin":{"type":"string"}},"additionalProperties":false}"""),
            Tool("calendar_get_event", true, """{"type":"object","properties":{"eventId":{"type":"string"}},"required":["eventId"],"additionalProperties":false}""")
        ] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id,
            ["google:calendar/calendar_list_events", "google:calendar/calendar_get_event"], default);
        var parent = await f.ConfirmedParent(connection.Id, "google:calendar");
        var registry = new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance);
        var executor = new McpSkillExecutor(registry, f.Service, new Executions(), NullLogger<McpSkillExecutor>.Instance);
        var args = JsonSerializer.SerializeToElement(new { steps = new object[]
        {
            new { skillId = "calendar.get_upcoming_meetings", arguments = new { } },
            new { skillId = "calendar.get_meeting_details", arguments = new { eventId = "event-1" } }
        } });
        var result = await executor.ExecuteAsync(new(f.Owner, f.Session, connection.Id, parent.Id,
            "workflow.find_meeting_link", args), default);
        Assert.Equal("completed", result.Status);
        Assert.Equal(2, f.Mcp.Calls);
        f.Mcp.FailToolName = "calendar_get_event";
        var partial = await executor.ExecuteAsync(new(f.Owner, f.Session, connection.Id, parent.Id,
            "workflow.find_meeting_link", args), default);
        Assert.Equal("partially_failed", partial.Status);
        Assert.Equal("fixture_tool_failed", partial.ErrorCode);
        Assert.Equal(1, partial.Output!.Value.GetProperty("completed").GetArrayLength());
    }

    [Fact]
    public async Task FreeSlotsAreComputedFromAllDiscoveredBusyIntervals()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] = [Tool("calendar_free_busy", true,
            """{"type":"object","properties":{"timeMin":{"type":"string"},"timeMax":{"type":"string"},"calendarIds":{"type":"array","items":{"type":"string"}}},"required":["timeMin","timeMax"],"additionalProperties":false}""")] };
        f.Mcp.Results = new() { ["calendar_free_busy"] = JsonSerializer.SerializeToElement(new { content = new[]
        { new { type = "text", text = """{"primary":{"busy":[{"start":"2026-10-26T10:00:00Z","end":"2026-10-26T11:00:00Z"}]},"other":{"busy":[{"start":"2026-10-26T11:30:00Z","end":"2026-10-26T12:00:00Z"}]}}""" } } }) };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:calendar/calendar_free_busy"], default);
        var parent = await f.ConfirmedParent(connection.Id, "google:calendar");
        var executor = new McpSkillExecutor(new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance),
            f.Service, new Executions(), NullLogger<McpSkillExecutor>.Instance);
        var result = await executor.ExecuteAsync(new(f.Owner, f.Session, connection.Id, parent.Id, "calendar.find_free_slots",
            JsonSerializer.SerializeToElement(new { timeMin = "2026-10-26T09:00:00Z", timeMax = "2026-10-26T13:00:00Z",
                durationMinutes = 30 })), default);
        Assert.Equal("completed", result.Status);
        Assert.Equal(3, result.Output!.Value.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task DestructiveWorkflowStepNeedsApprovalAndResumesWithoutRepeatingRead()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] =
        [
            Tool("calendar_get_event", false, """{"type":"object","properties":{"eventId":{"type":"string"}},"required":["eventId"],"additionalProperties":false}"""),
            Tool("calendar_delete_event", true, """{"type":"object","properties":{"eventId":{"type":"string"}},"required":["eventId"],"additionalProperties":false}""")
        ] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id,
            ["google:calendar/calendar_get_event", "google:calendar/calendar_delete_event"], default);
        var parent = await f.ConfirmedParent(connection.Id, "google:calendar");
        var records = new Executions();
        var executor = new McpSkillExecutor(new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance),
            f.Service, records, NullLogger<McpSkillExecutor>.Instance, f.Core.Actions,
            Options.Create(new McpSettings { CredentialEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        var args = JsonSerializer.SerializeToElement(new { steps = new object[]
        {
            new { skillId = "calendar.get_event", arguments = new { eventId = "event-1" } },
            new { skillId = "calendar.delete_event", arguments = new { eventId = "event-1", targetScope = "series" } }
        } });
        var initial = await executor.ExecuteAsync(new(f.Owner, f.Session, connection.Id, parent.Id,
            "workflow.cancel_meeting", args), default);
        Assert.Equal("awaiting_approval", initial.Status);
        Assert.NotNull((await records.GetAsync(initial.ExecutionId!, default))!.EncryptedPlan);
        parent = (await f.Core.Actions.GetAsync(parent.Id, default))!;
        parent.Status = QuacktionStatus.WaitingForTools;
        parent.ChildTaskIds = [initial.ApprovalTaskId!];
        Assert.True(await f.Core.Actions.ReplaceAsync(parent, default));
        var worker = new QuacktionExecutionService(f.Core.Actions, f.Core.Accounts, new ActionRiskEvaluator(),
            f.Core.Agent, f.Core.Events, NullLogger<QuacktionExecutionService>.Instance, records, executor);
        Assert.Equal(1, f.Mcp.Calls);
        // Simulate a crash after the destructive child was proposed but before its ID was saved.
        parent.ChildTaskIds.Clear();
        Assert.True(await f.Core.Actions.ReplaceAsync(parent, default));
        await worker.ExecuteNextAsync(default);
        Assert.Contains(initial.ApprovalTaskId!, (await f.Core.Actions.GetAsync(parent.Id, default))!.ChildTaskIds);
        await CompleteChild(f, initial.ApprovalTaskId!);
        await worker.ExecuteNextAsync(default);
        Assert.Equal(QuacktionStatus.Completed, (await f.Core.Actions.GetAsync(parent.Id, default))!.Status);
        Assert.Equal(2, f.Mcp.Calls);
        Assert.Null((await records.GetAsync(initial.ExecutionId!, default))!.EncryptedPlan);
    }

    private static async Task CompleteChild(McpConnectionTests.Harness f, string id)
    {
        var child = (await f.Core.Actions.GetAsync(id, default))!;
        await f.Core.Approvals.ResolveAsync(f.Owner, id, child.ActionDigest, true, default);
        child = (await f.Core.Actions.ClaimAsync(DateTime.UtcNow, TimeSpan.FromMinutes(1), default))!;
        Assert.Equal(id, child.Id);
        var result = await f.Service.ExecuteApprovedAsync(new("mcp.invoke", child.ParametersJson, child.IdempotencyKey,
            f.Owner, f.Session), default);
        Assert.True(result.Success);
        child.Status = QuacktionStatus.Completed;
        Assert.True(await f.Core.Actions.ReplaceAsync(child, default));
    }

    [Fact]
    public async Task CompleteEmailSendsWithoutReviewAndIsNotRepeated()
    {
        var f = await McpConnectionTests.Harness.Create();
        // The provider does not advertise this as read-only; the reviewed skill policy controls it.
        f.Mcp.ToolDefinitions = new() { ["google:gmail"] = [Tool("gmail_send_message", false,
            """{"type":"object","properties":{"to":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"},"body":{"type":"string"}},"required":["to","subject","body"],"additionalProperties":false}""")] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:gmail/gmail_send_message"], default);
        var parent = await f.ConfirmedParent(connection.Id, "google:gmail");
        var executor = new McpSkillExecutor(new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance),
            f.Service, new Executions(), NullLogger<McpSkillExecutor>.Instance);
        var request = new SkillExecutionRequest(f.Owner, f.Session, connection.Id, parent.Id, "gmail.send_email",
            JsonSerializer.SerializeToElement(new { to = new[] { "sarah@example.test" }, subject = "Plan", body = "Let's meet Friday." }), "send-1");
        var result = await executor.ExecuteAsync(request, default);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, f.Mcp.Calls);
        Assert.Null(result.ApprovalTaskId);
        Assert.Equal("already_attempted", (await executor.ExecuteAsync(request, default)).Status);
        Assert.Equal(1, f.Mcp.Calls);
        var secondInvocation = await executor.ExecuteAsync(request with { InvocationId = "send-2" }, default);
        Assert.Equal("failed", secondInvocation.Status);
        Assert.Equal("mcp_execution_already_attempted_reconcile_required", secondInvocation.ErrorCode);
        Assert.Equal(1, f.Mcp.Calls);
    }

    [Fact]
    public async Task CalendarDeleteRequiresExplicitRecurringScope()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Mcp.ToolDefinitions = new() { ["google:calendar"] = [Tool("calendar_delete_event", false,
            """{"type":"object","properties":{"eventId":{"type":"string"}},"required":["eventId"],"additionalProperties":false}""")] };
        var connection = await f.Connect();
        await f.Service.SetEnabledToolsAsync(f.Owner, connection.Id, ["google:calendar/calendar_delete_event"], default);
        var parent = await f.ConfirmedParent(connection.Id, "google:calendar");
        var executor = new McpSkillExecutor(new McpSkillRegistry(f.Service, NullLogger<McpSkillRegistry>.Instance),
            f.Service, new Executions(), NullLogger<McpSkillExecutor>.Instance);
        var invalid = await executor.ExecuteAsync(new(f.Owner, f.Session, connection.Id, parent.Id, "calendar.delete_event",
            JsonSerializer.SerializeToElement(new { eventId = "series_master", targetScope = "occurrence" })), default);
        Assert.Equal("recurring_occurrence_id_required", invalid.ErrorCode);
        var proposed = await executor.ExecuteAsync(new(f.Owner, f.Session, connection.Id, parent.Id, "calendar.delete_event",
            JsonSerializer.SerializeToElement(new { eventId = "series_master", targetScope = "series" })), default);
        Assert.Equal("awaiting_approval", proposed.Status);
        var child = await f.Core.Actions.GetAsync(proposed.ApprovalTaskId!, default);
        Assert.Contains("recurrenceScope=series", child!.ParametersJson);
    }

    private static McpToolDefinition Tool(string name, bool readOnly, string schema) =>
        new(name, null, JsonDocument.Parse(schema).RootElement.Clone(), readOnly, !readOnly);
}
