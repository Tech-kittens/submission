using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public record Evidence(string Title, string Url, DateTime RetrievedAt, string Summary, string[] References);
