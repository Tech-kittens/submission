using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class Account : Entity
{
    public string Email { get; set; } = "";
    [JsonIgnore] public string PasswordHash { get; set; } = "";
    [JsonIgnore] public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
