using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class Device : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string DeviceIdentifier { get; set; } = "";
    public string Name { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string DeviceType { get; set; } = "ESP32-S3";
    public DeviceStatus Status { get; set; }
    public DateTime? LastSeenAt { get; set; }
    [JsonIgnore] public string? CredentialHash { get; set; }
    [JsonIgnore] public string? ActiveSessionId { get; set; }
}
