using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class Session : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string? DeviceId { get; set; }
    public string Title { get; set; } = "";
    public SessionStatus Status { get; set; }
    public string? AgentSessionId { get; set; }
    public string? StartRequestId { get; set; }
    public string? InitialPrompt { get; set; }
    public string PromptFormat { get; set; } = "text";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public string? FailureCode { get; set; }
    [JsonIgnore] public DateTime? ConnectionCredentialExpiresAt { get; set; }
    [JsonIgnore] public DateTime? ConnectionCredentialConsumedAt { get; set; }
}
