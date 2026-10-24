using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public class QuacktionExecution : Entity
{
    public string QuacktionId { get; set; } = "";
    public string LeaseId { get; set; } = "";
    public int Attempt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Outcome { get; set; } = "InProgress";
    public string? FailureCode { get; set; }
}
