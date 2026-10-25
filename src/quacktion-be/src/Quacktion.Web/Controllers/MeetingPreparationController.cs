using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Preparation;
namespace Quacktion.Web.Controllers;

public sealed record ResearchUpdate(long ExpectedVersion, long CalendarRevision, [Required, MaxLength(128)] string RequestId, [Required] PreparationResearch Research);
public sealed record QuestionRequest([Required, MaxLength(128)] string RequestId, [Required, MaxLength(1000)] string Text);
public sealed record AnswerRequest([Required, MaxLength(4000)] string Answer);
public sealed record CalendarSubscriptionRequest([Required] string ConnectionId, [Required, MaxLength(1024)] string CalendarId);
[Route("api")]
public sealed class MeetingPreparationController(PreparationStore store, IMeetingMemorySearchService search,
    IMongoDbContext db, RelationshipMemory relationships) : OwnedController
{
    public sealed record PreparationLinkRequest([Required] string PreparationId);
    [HttpPost("meetings/{id}/preparation-link")]
    public async Task<IActionResult> Link(string id, PreparationLinkRequest request, CancellationToken ct)
    {
        var p = await store.GetAsync(Owner, request.PreparationId, ct);
        var result = await db.Collection<Meeting>().UpdateOneAsync(x => x.Id == id && x.OwnerAccountId == Owner,
            Builders<Meeting>.Update.Set(x => x.ParticipantEmails, p.Attendees.Select(x => x.Email.ToLowerInvariant()).Distinct().ToArray())
                .Set(x => x.PreparationId, p.Id).Set(x => x.MemoryGeneration, null).Set(x => x.MemoryIndexedAt, null), cancellationToken: ct);
        if (result.MatchedCount == 0) return NotFound();
        return NoContent();
    }
    [HttpGet("meeting-memory/relationships")]
    public async Task<IActionResult> Relationship([FromQuery, Required] string email, CancellationToken ct) => Ok(await relationships.GetAsync(Owner, email, ct));
    [HttpPost("meeting-memory/search")]
    public async Task<IActionResult> Search(MeetingMemorySearchRequest request, CancellationToken ct) => Ok(await search.SearchAsync(Owner, request, ct));
    [HttpPost("meeting-memory/backfill")]
    public async Task<IActionResult> Backfill(CancellationToken ct)
    {
        await db.Collection<Meeting>().UpdateManyAsync(x => x.OwnerAccountId == Owner && x.Status == MeetingStatus.Completed,
            Builders<Meeting>.Update.Set(x => x.MemoryIndexedAt, null).Set(x => x.MemoryGeneration, null).Set(x => x.MemoryNextAttemptAt, null), cancellationToken: ct);
        return Accepted(new { status = "queued" });
    }
    [HttpGet("meeting-preparation/preferences")]
    public async Task<IActionResult> Preferences(CancellationToken ct) => Ok(await store.PreferencesAsync(Owner, ct));
    [HttpPut("meeting-preparation/preferences")]
    public async Task<IActionResult> Preferences(PreparationPreferences request, CancellationToken ct)
    { await store.SavePreferencesAsync(Owner, request, ct); return NoContent(); }
    [HttpPost("meeting-preparation/calendars")]
    public async Task<IActionResult> Subscribe(CalendarSubscriptionRequest request, CancellationToken ct)
    {
        var connection = Guard.Found(await db.Collection<UserMcpConnection>().Find(x => x.Id == request.ConnectionId
            && x.UserId == Owner && x.Status == "Connected").FirstOrDefaultAsync(ct));
        Guard.State(connection.EnabledServices.Contains("google:calendar"), "calendar_not_authorised");
        var state = new CalendarSyncState { OwnerAccountId = Owner, ConnectionId = connection.Id,
            GoogleAccountId = connection.AccountIdentityVerified ? connection.ProviderAccountId : "grant:" + connection.Id,
            CalendarId = request.CalendarId };
        state.Id = CalendarMeetingParser.Identity(Owner, state.GoogleAccountId, request.CalendarId, "sync");
        await db.Collection<CalendarSyncState>().UpdateOneAsync(x => x.Id == state.Id,
            Builders<CalendarSyncState>.Update.SetOnInsert(x => x.OwnerAccountId, Owner).SetOnInsert(x => x.ConnectionId, state.ConnectionId)
                .SetOnInsert(x => x.GoogleAccountId, state.GoogleAccountId).SetOnInsert(x => x.CalendarId, state.CalendarId),
            new UpdateOptions { IsUpsert = true }, ct);
        return Accepted(new { syncId = state.Id });
    }
    [HttpGet("meeting-preparation/calendars")]
    public async Task<IActionResult> Calendars(CancellationToken ct) => Ok(await db.Collection<CalendarSyncState>()
        .Find(x => x.OwnerAccountId == Owner).Limit(100).ToListAsync(ct));
    [HttpGet("meetings/upcoming")]
    public async Task<IActionResult> Upcoming(CancellationToken ct) => Ok(await store.Preparations.Find(x => x.OwnerAccountId == Owner
        && x.Status != "Cancelled" && x.EndsAt > DateTime.UtcNow).SortBy(x => x.StartsAt).Limit(100).ToListAsync(ct));
    [HttpPost("meetings/{id}/prepare")]
    public async Task<IActionResult> Prepare(string id, CancellationToken ct)
    {
        var p = await store.GetAsync(Owner, id, ct);
        Guard.State(p.Status != "Cancelled" && p.EndsAt > DateTime.UtcNow, "preparation_cancelled");
        p.Status = "Pending"; p.ResearchAttempts = 0; p.ResearchNextAttemptAt = DateTime.UtcNow;
        if (!await store.SaveAsync(p, ct)) throw new AppException(409, "preparation_conflict");
        return Accepted(new { p.Id, p.Status });
    }
    [HttpGet("meeting-preparations")]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await store.Preparations.Find(x => x.OwnerAccountId == Owner)
        .SortByDescending(x => x.StartsAt).Limit(100).ToListAsync(ct));
    [HttpGet("meetings/{id}/preparation")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) => Ok(await store.GetAsync(Owner, id, ct));
    [HttpPut("meetings/{id}/preparation")]
    public async Task<IActionResult> Update(string id, ResearchUpdate request, CancellationToken ct)
    { await store.UpdateResearchAsync(Owner, id, request.ExpectedVersion, request.CalendarRevision, request.RequestId, request.Research, false, ct); return NoContent(); }
    [HttpPost("meetings/{id}/preparation/complete")]
    public async Task<IActionResult> Complete(string id, ResearchUpdate request, CancellationToken ct)
    { await store.UpdateResearchAsync(Owner, id, request.ExpectedVersion, request.CalendarRevision, request.RequestId, request.Research, true, ct); return NoContent(); }
    [HttpPost("meetings/{id}/preparation/questions")]
    public async Task<IActionResult> Question(string id, QuestionRequest request, CancellationToken ct) =>
        Ok(await store.QuestionAsync(Owner, id, request.RequestId, request.Text, ct));
    [HttpPost("meetings/{id}/preparation/questions/{questionId}/responses")]
    public async Task<IActionResult> Answer(string id, string questionId, AnswerRequest request, CancellationToken ct)
    { await store.AnswerAsync(Owner, id, questionId, request.Answer, ct); return NoContent(); }
    [HttpPost("meetings/{id}/preparation/events/{eventId}/retry")]
    public async Task<IActionResult> Retry(string id, string eventId, CancellationToken ct)
    {
        var p = await store.GetAsync(Owner, id, ct); var e = Guard.Found(p.Outbox.Find(x => x.Id == eventId));
        Guard.State(e.Status == "DeadLetter" && e.CalendarRevision == p.CalendarRevision && p.Status != "Cancelled", "event_not_retryable");
        e.Status = "Pending"; e.Attempts = 0; e.NextAttemptAt = DateTime.UtcNow;
        if (!await store.SaveAsync(p, ct)) throw new AppException(409, "preparation_conflict");
        return Accepted();
    }
}
