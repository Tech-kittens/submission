using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class ApprovalRequest : Entity
{
    public string QuacktionId { get; set; } = "";
    public string OwnerAccountId { get; set; } = "";
    public string ActionDigest { get; set; } = "";
    public ApprovalStatus Status { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
