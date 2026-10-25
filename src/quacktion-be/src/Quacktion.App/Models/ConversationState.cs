using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

// Companion to sessions: same ID, independently checkpointed transport ownership.
public sealed class ConversationState : Entity
{
    public string OwnerAccountId { get; set; } = "";
    [JsonIgnore] public string? LeaseId { get; set; }
    public DateTime LeaseExpiresAt { get; set; }
    public DateTime LastCheckpointAt { get; set; }
    public long Sequence { get; set; }
}
public sealed class ConversationEvent : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public long Sequence { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}
public sealed class DeviceTransportState : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string? InstanceId { get; set; }
    [JsonIgnore] public string? LeaseId { get; set; }
    public DateTime LeaseExpiresAt { get; set; }
    public DateTime LastCheckpointAt { get; set; }
    public string? GapId { get; set; }
}
