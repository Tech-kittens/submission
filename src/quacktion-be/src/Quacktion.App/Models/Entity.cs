using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public abstract class Entity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Version { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
