using System.Text.Json;

namespace Quacktion.App.Models;

public enum SkillPermissionLevel { Read, Write, ExternalCommunication, Destructive }

public sealed class McpSkillDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Provider { get; set; } = "google";
    public string Service { get; set; } = "";
    public string? ServerId { get; set; }
    public string? ToolName { get; set; }
    public string[] RequiredCapabilities { get; set; } = [];
    public JsonElement InputSchema { get; set; }
    public JsonElement OutputSchema { get; set; }
    public SkillPermissionLevel PermissionLevel { get; set; }
    public bool RequiresApproval { get; set; }
    public bool Available { get; set; }
    public string? UnavailableReason { get; set; }
    public string? ConnectionId { get; set; }
}

public sealed record SkillExecutionRequest(string OwnerAccountId, string SessionId, string ConnectionId,
    string ParentTaskId, string SkillId, JsonElement Arguments, string? InvocationId = null);

public sealed record SkillExecutionResult(string Status, JsonElement? Output = null, string? ErrorCode = null,
    string? ApprovalTaskId = null, string? ExecutionId = null);

public sealed class McpSkillExecution : Entity
{
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ParentTaskId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string SkillId { get; set; } = "";
    public string Status { get; set; } = "Created";
    public string? ApprovalTaskId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ResultSummary { get; set; }
    public string? EncryptedPlan { get; set; }
    public int NextStepIndex { get; set; }
    public DateTime? CompletedAt { get; set; }
}
