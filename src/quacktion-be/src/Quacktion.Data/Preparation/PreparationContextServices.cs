using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public static class PreparationBriefing
{
    public static string Render(MeetingPreparation p)
    {
        var text = new StringBuilder("QUACKTION — MEETING BRIEFING\n\n");
        text.AppendLine($"Meeting: {p.Title}");
        text.AppendLine($"Starts: {p.StartsAt:O} ({p.Timezone})");
        text.AppendLine("Attendees: " + string.Join(", ", p.Attendees.Select(x => x.Name is { Length: > 0 } ? $"{x.Name} <{x.Email}>" : x.Email)));
        text.AppendLine("Objective: " + (p.Research.Objective.Length > 0 ? p.Research.Objective : "Not yet confirmed."));
        text.AppendLine("Summary: " + (p.Research.Summary.Length > 0 ? p.Research.Summary : "Research is still pending; this briefing contains available calendar context."));
        void Section(string title, IEnumerable<string> items)
        {
            var lines = items.Take(8).ToArray(); if (lines.Length == 0) return;
            text.AppendLine("\n" + title + ":");
            foreach (var line in lines) text.AppendLine("- " + line);
        }
        Section("Previous context", p.Research.PreviousMeetings.Select(x => x.Summary));
        Section("Outstanding commitments", p.Research.OutstandingCommitments);
        Section("Risks", p.Research.Risks.Select(x => x.Description));
        Section("Suggested questions", p.Research.SuggestedQuestions);
        Section("Suggested agenda", p.Research.SuggestedAgenda);
        return text.ToString();
    }
}
public sealed class PreparationEmailContext(PreparationStore store, McpConnectionService connections, IOptions<PreparationSettings> options)
{
    public async Task<JsonElement> SearchAsync(string owner, string meetingId, string query, CancellationToken ct)
    {
        var prefs = await store.PreferencesAsync(owner, ct);
        Guard.State(prefs.Enabled && prefs.GmailEnabled, "preparation_gmail_not_authorised");
        Guard.Input(!string.IsNullOrWhiteSpace(query) && query.Length <= 1000, "gmail_query_invalid");
        var p = await store.GetAsync(owner, meetingId, ct);
        Guard.State(p.Status != "Cancelled", "preparation_cancelled");
        var tool = (await connections.ListToolsAsync(owner, p.ConnectionId, ct))
            .SingleOrDefault(x => x.ServerId == "google:gmail" && x.Name == options.Value.GmailSearchTool && x.ReadOnly)
            ?? throw new AppException(503, "gmail_search_tool_not_discovered");
        var properties = tool.InputSchema.GetProperty("properties");
        // The adapter supports either documented query convention only if that field is actually discovered.
        var key = properties.TryGetProperty("query", out _) ? "query" : properties.TryGetProperty("q", out _) ? "q" : null;
        if (key is null) throw new AppException(503, "gmail_search_schema_unsupported");
        var args = new Dictionary<string, object?> { [key] = query };
        if (properties.TryGetProperty("maxResults", out _)) args["maxResults"] = 10;
        return await connections.ExecutePreparationReadAsync(owner, p.ConnectionId, "google:gmail", tool.Name,
            JsonSerializer.SerializeToElement(args), options.Value.GmailRequiredScopes, ct);
    }
}
public sealed class RelationshipMemory(IMongoDbContext db)
{
    public async Task<object> GetAsync(string owner, string email, CancellationToken ct)
    {
        Guard.Input(email.Length is > 3 and <= 254 && email.Contains('@'), "participant_email_invalid");
        email = email.Trim().ToLowerInvariant();
        var meetings = await db.Collection<Meeting>().Find(x => x.OwnerAccountId == owner && x.ParticipantEmails.Contains(email)
            && x.Status == MeetingStatus.Completed).SortByDescending(x => x.CreatedAt).Limit(100).ToListAsync(ct);
        var ids = meetings.Select(x => x.Id).ToArray();
        var analyses = await db.Collection<MeetingAnalysis>().Find(x => x.OwnerAccountId == owner && ids.Contains(x.MeetingId)).Limit(100).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var upcoming = await db.Collection<MeetingPreparation>().Find(x => x.OwnerAccountId == owner && x.StartsAt > now
            && x.Status != "Cancelled" && x.Attendees.Any(a => a.Email == email)).SortBy(x => x.StartsAt).Limit(10).ToListAsync(ct);
        return new { participantEmail = email, previousMeetingCount = meetings.Count, boundedToRecentMeetings = 100,
            outstandingActionCount = analyses.Sum(x => x.ActionItems.Count(a => a.Status is not ("completed" or "cancelled" or "done"))),
            lastMeetingId = meetings.FirstOrDefault()?.Id,
            lastConfirmedDecisions = analyses.OrderByDescending(x => x.AnalysedAt).SelectMany(x => x.Decisions.Where(d => d.Status == "confirmed")
                .Select(d => new { x.MeetingId, d.Text, d.EvidenceSegmentIds })).Take(10),
            upcomingMeetings = upcoming.Select(x => new { x.Id, x.Title, x.StartsAt }) };
    }
}
