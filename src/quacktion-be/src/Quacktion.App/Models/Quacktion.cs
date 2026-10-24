using System.Text.Json.Serialization;

namespace Quacktion.App.Models;

public class Quacktion : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string ActionDigest { get; set; } = "";
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public QuacktionStatus Status { get; set; }
    public ApprovalRequest? Approval { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LeaseId { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public bool CancellationRequested { get; set; }
    public string? ResultJson { get; set; }
    public string? FailureCode { get; set; }
    public List<QuacktionExecution> History { get; set; } = [];
}
