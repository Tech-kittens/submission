using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class PairingChallenge : Entity
{
    public string DeviceId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}
