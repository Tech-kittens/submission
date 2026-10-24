using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public record ResearchResult(string Summary, IReadOnlyList<Evidence> Sources, string Assessment);
