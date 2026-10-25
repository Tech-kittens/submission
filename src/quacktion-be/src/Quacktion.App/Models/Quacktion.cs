using System.Text.Json.Serialization;

namespace Quacktion.App.Models;

public class Quacktion : Entity
{
    public QuacktionStatus? NotificationStatus { get; set; }
    public int NotificationGeneration { get; set; }
    public DateTime NotificationAt { get; set; }
    public string OwnerAccountId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string ActionDigest { get; set; } = "";
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public QuacktionStatus Status { get; set; }
    public ApprovalRequest? Approval { get; set; }
    public bool AutoAuthorizedMcp { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LeaseId { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public bool CancellationRequested { get; set; }
    public string? ResultJson { get; set; }
    public string? FailureCode { get; set; }
    public string AnnouncementPolicy { get; set; } = "APPROVAL_REQUIRED";
    public List<string> ChildTaskIds { get; set; } = [];
    public List<QuacktionExecution> History { get; set; } = [];
}
