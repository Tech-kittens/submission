using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class RefreshToken : Entity
{
    public string AccountId { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string CurrentHash { get; set; } = "";
    public List<string> UsedHashes { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}
