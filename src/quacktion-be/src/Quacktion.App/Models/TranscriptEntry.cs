using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class TranscriptEntry : Entity
{
    public string SessionId { get; set; } = "";
    public string? SpeakerIdentifier { get; set; }
    public string Text { get; set; } = "";
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string Source { get; set; } = "";
    public string SourceEventId { get; set; } = "";
}
