using System.Text.Json.Serialization;

namespace Quacktion.App.Models;

public class Agent : Entity
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string[] Capabilities { get; set; } = [];
    public bool Enabled { get; set; }
}
