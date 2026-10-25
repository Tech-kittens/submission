using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public sealed class PreparationStore(IMongoDbContext db, IMeetingMemorySearchService search, ILogger<PreparationStore>? logger = null) : IPreparationContext
{
    public Task<bool> OwnerActiveAsync(string owner, CancellationToken ct) =>
        db.Collection<Account>().Find(x => x.Id == owner && x.IsActive).AnyAsync(ct);
    public IMongoCollection<MeetingPreparation> Preparations => db.Collection<MeetingPreparation>();
    public async Task<PreparationPreferences> PreferencesAsync(string owner, CancellationToken ct) =>
        await db.Collection<PreparationPreferences>().Find(x => x.OwnerAccountId == owner).FirstOrDefaultAsync(ct)
        ?? new() { Id = owner, OwnerAccountId = owner };
    public async Task SavePreferencesAsync(string owner, PreparationPreferences preferences, CancellationToken ct)
    {
        Guard.Input(preferences.BriefingMinutes is >= 1 and <= 1440 && preferences.RetentionDays is >= 1 and <= 365
            && preferences.ExcludedCalendarIds.Length <= 100 && preferences.ExcludedEventIds.Length <= 1000, "invalid_preparation_preferences");
        preferences.Id = owner; preferences.OwnerAccountId = owner;
        await db.Collection<PreparationPreferences>().ReplaceOneAsync(x => x.Id == owner, preferences, new ReplaceOptions { IsUpsert = true }, ct);
        // Recalculate durable schedules, including changes made after a briefing was queued.
        var now = DateTime.UtcNow;
        var pending = await Preparations.Find(x => x.OwnerAccountId == owner && x.StartsAt > now
            && x.Status != "Cancelled" && x.BriefingStatus != "Delivered" && x.BriefingStatus != "AcknowledgedSilent").Limit(1000).ToListAsync(ct);
        foreach (var p in pending)
        {
            foreach (var e in p.Outbox.Where(x => x.Status is "Pending" or "Delivered" && x.Type == "meeting.briefing_requested")) e.Status = "Cancelled";
            p.BriefingDueAt = p.StartsAt.AddMinutes(-preferences.BriefingMinutes);
            p.BriefingStatus = preferences.Enabled && preferences.MessagingOptIn ? "Pending" : "Suppressed";
            if (!await SaveAsync(p, ct)) throw new AppException(409, "preference_schedule_conflict_retry");
        }
    }
    public async Task<MeetingPreparation> GetAsync(string owner, string id, CancellationToken ct) =>
        Guard.Found(await Preparations.Find(x => x.Id == id && x.OwnerAccountId == owner).FirstOrDefaultAsync(ct));
    public async Task<bool> SaveAsync(MeetingPreparation preparation, CancellationToken ct)
    {
        var version = preparation.Version; preparation.Version++; preparation.UpdatedAt = DateTime.UtcNow;
        var result = await Preparations.ReplaceOneAsync(x => x.Id == preparation.Id && x.OwnerAccountId == preparation.OwnerAccountId
            && x.Version == version, preparation, cancellationToken: ct);
        if (result.ModifiedCount == 1) return true;
        preparation.Version = version; return false;
    }
    public async Task InsertAsync(MeetingPreparation preparation, CancellationToken ct)
    {
        try { await Preparations.InsertOneAsync(preparation, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw new AppException(409, "preparation_conflict"); }
    }
    public static void Event(MeetingPreparation preparation, string type, string? questionId = null)
    {
        preparation.Outbox.Add(new() { Type = type, CalendarRevision = preparation.CalendarRevision, QuestionId = questionId });
        // Keep bounded delivery history, never remove undelivered or dead-letter events.
        var delivered = preparation.Outbox.Where(x => x.Status is "Delivered" or "Cancelled").OrderByDescending(x => x.OccurredAt).Skip(50).ToHashSet();
        preparation.Outbox.RemoveAll(delivered.Contains);
        Guard.State(preparation.Outbox.Count <= 250, "preparation_outbox_full");
    }
    public async Task UpdateResearchAsync(string owner, string id, long expectedVersion, long calendarRevision,
        string requestId, PreparationResearch research, bool complete, CancellationToken ct)
    {
        var p = await GetAsync(owner, id, ct);
        if (p.LastRequestId == requestId) return;
        Guard.State(p.Version == expectedVersion && p.CalendarRevision == calendarRevision && p.Status != "Cancelled", "preparation_revision_conflict");
        Guard.Input(requestId is { Length: > 0 and <= 128 } && research is not null
            && JsonSerializer.Serialize(research).Length <= 32000, "preparation_input_invalid");
        Guard.Input(research!.Summary is not null && research.Objective is not null && research.Attendees is not null
            && research.PreviousMeetings is not null && research.Decisions is not null && research.OutstandingCommitments is not null
            && research.OpenQuestions is not null && research.Risks is not null && research.SuggestedAgenda is not null
            && research.SuggestedQuestions is not null && research.Sources is not null, "preparation_input_invalid");
        Guard.Input(research.Attendees!.All(x => x is not null && x.SourceIds is not null && x.Summary is not null)
            && research.PreviousMeetings!.All(x => x is not null && x.SourceIds is not null && x.Summary is not null)
            && research.Risks!.All(x => x is not null && x.SourceIds is not null && x.Description is not null)
            && research.Sources!.All(x => x is not null && x.Id is not null && x.Reference is not null), "preparation_input_invalid");
        Guard.Input(research.Sources!.All(x => x.Kind is "meeting" or "email" or "public" or "user")
            && research.Attendees!.All(x => p.Attendees.Any(a => string.Equals(a.Email, x.Email, StringComparison.OrdinalIgnoreCase))), "preparation_sources_invalid");
        p.Revisions.Add(new(p.Version, DateTime.UtcNow, requestId, p.Research));
        if (p.Revisions.Count > 10) p.Revisions.RemoveAt(0);
        p.Research = research; p.LastRequestId = requestId; p.Status = complete ? "Ready" : "Researching";
        Event(p, "meeting.preparation_updated");
        if (!await SaveAsync(p, ct)) throw new AppException(409, "preparation_revision_conflict");
        logger?.LogInformation("MeetingPreparationSaved {MeetingId} {Revision} {Status} {ElapsedSeconds}", p.Id,
            p.CalendarRevision, p.Status, (DateTime.UtcNow - p.CreatedAt).TotalSeconds);
    }
    public async Task<PreparationQuestion> QuestionAsync(string owner, string id, string requestId, string text, CancellationToken ct)
    {
        var p = await GetAsync(owner, id, ct); var preferences = await PreferencesAsync(owner, ct);
        var questionId = Secrets.Digest(id, requestId);
        var prior = p.Questions.Find(x => x.Id == questionId);
        if (prior is not null) { Guard.State(prior.Text == text, "idempotency_conflict"); return prior; }
        Guard.Input(!string.IsNullOrWhiteSpace(text) && text.Length <= 1000 && requestId.Length is > 0 and <= 128, "question_invalid");
        Guard.State(preferences.Enabled && preferences.MessagingOptIn && preferences.ClarificationsEnabled
            && p.Status != "Cancelled" && DateTime.UtcNow < p.StartsAt && p.Questions.Count < 2, "clarification_not_allowed");
        Guard.State(p.Questions.Count == 0 || p.Questions[0].AskedAt <= DateTime.UtcNow.AddHours(-4), "clarification_rate_limited");
        var q = new PreparationQuestion { Id = questionId, Text = text, AskedAt = DateTime.UtcNow }; p.Questions.Add(q);
        Event(p, "meeting.clarification_requested", q.Id);
        if (!await SaveAsync(p, ct)) throw new AppException(409, "preparation_revision_conflict");
        return q;
    }
    public async Task AnswerAsync(string owner, string id, string questionId, string answer, CancellationToken ct)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(answer) && answer.Length <= 4000, "answer_invalid");
        var p = await GetAsync(owner, id, ct);
        var q = Guard.Found(p.Questions.Find(x => x.Id == questionId));
        if (q.Answer == answer) return;
        Guard.State(q.Answer is null, "question_already_answered");
        q.Answer = answer; q.AnsweredAt = DateTime.UtcNow; q.LateReply = q.AnsweredAt >= p.StartsAt;
        if (!q.LateReply && p.Status != "Cancelled") { p.Status = "Researching"; Event(p, "meeting.preparation_requested"); }
        if (!await SaveAsync(p, ct)) throw new AppException(409, "preparation_revision_conflict");
    }
    public async Task<string> RetrieveAsync(string owner, string query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var preparations = await Preparations.Find(x => x.OwnerAccountId == owner && x.Status != "Cancelled" && x.EndsAt > now)
            .SortBy(x => x.StartsAt).Limit(10).ToListAsync(ct);
        var memories = await search.SearchAsync(owner, new() { Query = query[..Math.Min(2000, query.Length)], Limit = 8 }, ct);
        return JsonSerializer.Serialize(new { preparations = preparations.Select(x => new { x.Id, x.Title, x.StartsAt, x.Attendees,
            summary = x.Research.Summary[..Math.Min(2000, x.Research.Summary.Length)], x.Research.Objective,
            suggestedQuestions = x.Research.SuggestedQuestions.Take(8), agenda = x.Research.SuggestedAgenda.Take(8),
            commitments = x.Research.OutstandingCommitments.Take(8) }), memories });
    }
}
