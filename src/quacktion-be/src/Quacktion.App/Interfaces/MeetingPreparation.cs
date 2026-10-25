using System.ComponentModel.DataAnnotations;
using Quacktion.App.Models;
namespace Quacktion.App.Interfaces;

public sealed class MeetingMemorySearchRequest
{
    [Required, StringLength(2000, MinimumLength = 1)] public string Query { get; set; } = "";
    [MaxLength(30)] public string[] ParticipantEmails { get; set; } = [];
    [MaxLength(10)] public string[] DocumentTypes { get; set; } = [];
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    [StringLength(100)] public string? MeetingType { get; set; }
    [Range(1, 30)] public int Limit { get; set; } = 10;
}
public sealed record MeetingMemoryHit(string Id, string MeetingId, string MeetingTitle, DateTime MeetingDate,
    string Type, string Content, string[] ParticipantEmails, string[] SourceSegmentIds, double Score);
public sealed record MeetingMemorySearchResult(IReadOnlyList<MeetingMemoryHit> Results, string Ranking);
public interface IMeetingMemorySearchService
{
    Task<MeetingMemorySearchResult> SearchAsync(string authenticatedOwner, MeetingMemorySearchRequest request, CancellationToken ct);
}
public interface IMeetingEmbeddingService
{
    string Model { get; }
    int Dimensions { get; }
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct, bool query = false);
}
public interface IMeetingMemoryLifecycle
{
    Task DeleteAsync(string owner, string meetingId, CancellationToken ct);
}
public interface IPreparationContext
{
    Task<string> RetrieveAsync(string owner, string query, CancellationToken ct);
}

public sealed record MeetingPreparationResult(string MeetingId, string Status);
public interface IMeetingPreparationAgent
{
    Task<MeetingPreparationResult> PrepareAsync(string meetingId, CancellationToken cancellationToken);
}
public interface IBriefingConversation
{
    Task<string> ExecuteAsync(string owner, string sessionId, string operation, string? meetingId, CancellationToken ct, string? query = null);
    Task<string?> ObserveAsync(string owner, string sessionId, string text, CancellationToken ct, bool? assistantWasSpeaking = null);
}
