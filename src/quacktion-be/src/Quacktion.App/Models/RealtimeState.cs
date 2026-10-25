using System.Text.Json.Serialization;

namespace Quacktion.App.Models;

// Separate documents avoid racing optimistic updates to the authoritative meeting record.
public sealed class RealtimeState : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string SessionId { get; set; } = "";
    [JsonIgnore] public string? ResumptionHandle { get; set; }
    [JsonIgnore] public string? EncryptedResumptionHandle { get; set; }
    public DateTime HandleUpdatedAt { get; set; }
}

public sealed class MeetingToolCall : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string? QuacktionId { get; set; }
    public string? ResultJson { get; set; }
    public string? AnnouncementQuacktionId { get; set; }
    public string ResponseScheduling { get; set; } = "WHEN_IDLE";
    public string ProgressStatus { get; set; } = "accepted";
    public bool Cancelled { get; set; }
    public bool Delivered { get; set; }
    public bool ProgressDelivered { get; set; }
}
