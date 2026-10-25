using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public sealed record CalendarMeeting(string Id, string? RecurringEventId, string Title, string Description,
    DateTime StartsAt, DateTime EndsAt, string Timezone, List<PreparationAttendee> Attendees, bool Cancelled, bool Declined, bool AllDay, string Status = "confirmed");
public static class CalendarMeetingParser
{
    public static JsonElement Unwrap(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object) return structured.Clone();
        if (result.TryGetProperty("content", out var content))
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("text", out var text))
                { try { return JsonDocument.Parse(text.GetString()!).RootElement.Clone(); } catch (JsonException) { } }
        return result;
    }
    public static CalendarMeeting Parse(JsonElement e)
    {
        string S(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var id = S(e, "id"); Guard.Input(id.Length > 0, "calendar_event_id_missing");
        var cancelled = S(e, "status") == "cancelled";
        var attendees = new List<PreparationAttendee>(); var declined = false;
        if (e.TryGetProperty("attendees", out var array)) foreach (var a in array.EnumerateArray())
        {
            if (a.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True && S(a, "responseStatus") == "declined") declined = true;
            if (S(a, "email") is { Length: > 0 } email) attendees.Add(new(email.ToLowerInvariant(), S(a, "displayName"), S(a, "responseStatus")));
        }
        if (e.TryGetProperty("organizer", out var organizer) && S(organizer, "email") is { Length: > 0 } organizerEmail
            && !attendees.Any(a => string.Equals(a.Email, organizerEmail, StringComparison.OrdinalIgnoreCase)))
            attendees.Add(new(organizerEmail.ToLowerInvariant(), S(organizer, "displayName")));
        var starts = DateTime.MinValue; var ends = DateTime.MinValue; var timezone = "UTC"; var allDay = false;
        if (e.TryGetProperty("start", out var start))
        {
            allDay = !start.TryGetProperty("dateTime", out _);
            if (DateTimeOffset.TryParse(S(start, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) starts = date.UtcDateTime;
            if (S(start, "timeZone").Length > 0) timezone = S(start, "timeZone");
        }
        if (e.TryGetProperty("end", out var end) && DateTimeOffset.TryParse(S(end, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate)) ends = endDate.UtcDateTime;
        return new(id, S(e, "recurringEventId"), S(e, "summary"), S(e, "description"), starts, ends, timezone, attendees.OrderBy(x => x.Email, StringComparer.Ordinal).ToList(), cancelled, declined, allDay, S(e, "status"));
    }
    public static bool Relevant(CalendarMeeting e, PreparationPreferences prefs) => prefs.Enabled && !e.Cancelled && !e.Declined
        && !e.AllDay && e.StartsAt != DateTime.MinValue && e.EndsAt > e.StartsAt && e.Title.Length > 0
        && (e.Attendees.Count > 1 || e.Description.Length >= 40) && !prefs.ExcludedEventIds.Contains(e.Id);
    public static string Identity(string owner, string account, string calendar, string id) => Secrets.Digest("preparation", JsonSerializer.Serialize(new[] { owner, account, calendar, id }));
}
public sealed class CalendarPreparationSync(IMongoDbContext db, PreparationStore store, McpConnectionService connections,
    IOptions<PreparationSettings> options, ILogger<CalendarPreparationSync> logger)
{
    public async Task ApplyAsync(CalendarSyncState state, CalendarMeeting e, PreparationPreferences preferences, CancellationToken ct)
    {
        var id = CalendarMeetingParser.Identity(state.OwnerAccountId, state.GoogleAccountId, state.CalendarId, e.Id);
        var p = await store.Preparations.Find(x => x.Id == id && x.OwnerAccountId == state.OwnerAccountId).FirstOrDefaultAsync(ct);
        var relevant = CalendarMeetingParser.Relevant(e, preferences) && !preferences.ExcludedCalendarIds.Contains(state.CalendarId);
        if (!relevant || e.EndsAt <= DateTime.UtcNow)
        {
            if (p is null || p.Status == "Cancelled") return;
            p.Status = "Cancelled"; p.CalendarRevision++; p.BriefingStatus = "Cancelled";
            foreach (var old in p.Outbox.Where(x => x.Status == "Pending")) old.Status = "Cancelled";
            PreparationStore.Event(p, "meeting.cancelled");
            if (!await store.SaveAsync(p, ct)) throw new AppException(409, "preparation_conflict");
            return;
        }
        var fingerprint = Secrets.Digest("calendar", JsonSerializer.Serialize(e));
        if (p?.CalendarFingerprint == fingerprint) return;
        var created = p is null;
        p ??= new() { Id = id, OwnerAccountId = state.OwnerAccountId, ConnectionId = state.ConnectionId,
            GoogleAccountId = state.GoogleAccountId, CalendarId = state.CalendarId, CalendarEventId = e.Id };
        if (!created)
        {
            p.Revisions.Add(new(p.Version, DateTime.UtcNow, "calendar:" + p.CalendarRevision, p.Research));
            if (p.Revisions.Count > 10) p.Revisions.RemoveAt(0);
            p.CalendarRevision++;
            p.Research = new(); // Never present research for the old agenda/time as current.
            p.LastRequestId = null;
        }
        foreach (var old in p.Outbox.Where(x => x.Status == "Pending")) old.Status = "Cancelled";
        p.RecurringEventId = e.RecurringEventId; p.Title = e.Title; p.Description = e.Description;
        p.StartsAt = e.StartsAt; p.EndsAt = e.EndsAt; p.Timezone = e.Timezone; p.Attendees = e.Attendees;
        p.ResearchAttempts = 0; p.ResearchNextAttemptAt = DateTime.UtcNow;
        p.CalendarFingerprint = fingerprint; p.Status = "Pending"; p.BriefingStatus = "Pending";
        p.BriefingDueAt = e.StartsAt.AddMinutes(-preferences.BriefingMinutes);
        p.ExpiresAt = e.EndsAt.AddDays(preferences.RetentionDays);
        PreparationStore.Event(p, created ? "meeting.created" : "meeting.updated");
        if (created) await store.InsertAsync(p, ct);
        else if (!await store.SaveAsync(p, ct)) throw new AppException(409, "preparation_conflict");
        logger.LogInformation("CalendarMeetingDetected {MeetingId} {CalendarRevision}", p.Id, p.CalendarRevision);
    }
    public async Task TickAsync(CancellationToken ct)
    {
        var states = db.Collection<CalendarSyncState>(); var now = DateTime.UtcNow;
        var state = await states.FindOneAndUpdateAsync(x => x.NextAttemptAt <= now && x.LeaseUntil < now,
            Builders<CalendarSyncState>.Update.Set(x => x.LeaseId, Guid.NewGuid().ToString("N")).Set(x => x.LeaseUntil, now.AddMinutes(5)),
            new FindOneAndUpdateOptions<CalendarSyncState> { ReturnDocument = ReturnDocument.After }, ct);
        if (state is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            var prefs = await store.PreferencesAsync(state.OwnerAccountId, timeout.Token);
            if (!prefs.Enabled) { state.NextAttemptAt = now.AddMinutes(5); return; }
            var tools = await connections.ListToolsAsync(state.OwnerAccountId, state.ConnectionId, timeout.Token);
            var tool = tools.SingleOrDefault(x => x.ServerId == "google:calendar" && x.Name == options.Value.CalendarListTool && x.ReadOnly)
                ?? throw new AppException(503, "calendar_list_tool_not_discovered");
            var properties = tool.InputSchema.GetProperty("properties");
            // Snapshot polling requires explicit cancellations and expanded recurring instances. Fail closed if unsupported.
            foreach (var field in new[] { "calendarId", "timeMin", "timeMax", "singleEvents", "showDeleted", "pageToken" })
                if (!properties.TryGetProperty(field, out _)) throw new AppException(503, "calendar_sync_schema_unsupported");
            var incremental = properties.TryGetProperty("syncToken", out _);
            // Re-bootstrap the moving horizon daily, including unchanged future occurrences.
            if (state.LastSnapshotAt is null || state.LastSnapshotAt < now.AddHours(-12)) state.SyncToken = null;
            var snapshot = state.SyncToken is null || !incremental;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? page = null; string? nextSync = null;
            var pages = 0;
            do
            {
                if (++pages > 100) throw new AppException(503, "calendar_sync_page_limit");
                var args = new Dictionary<string, object?> { ["calendarId"] = state.CalendarId, ["singleEvents"] = true, ["showDeleted"] = true };
                // A fixed full sync when tokens exist; rolling bounded snapshots otherwise.
                if (incremental && state.SyncToken is not null) args["syncToken"] = state.SyncToken;
                else { args["timeMin"] = now.AddDays(-1).ToString("O"); args["timeMax"] = now.AddDays(options.Value.HorizonDays).ToString("O"); }
                if (page is not null) args["pageToken"] = page;
                if (properties.TryGetProperty("maxResults", out _)) args["maxResults"] = 100;
                var raw = await connections.ExecutePreparationReadAsync(state.OwnerAccountId, state.ConnectionId,
                    "google:calendar", tool.Name, JsonSerializer.SerializeToElement(args), options.Value.CalendarRequiredScopes, timeout.Token);
                var result = CalendarMeetingParser.Unwrap(raw);
                if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new AppException(502, "calendar_response_schema_unsupported");
                foreach (var item in items.EnumerateArray())
                {
                    var e = CalendarMeetingParser.Parse(item);
                    seen.Add(e.Id);
                    if (e.StartsAt <= now.AddDays(options.Value.HorizonDays) || e.Cancelled)
                        await ApplyAsync(state, e, prefs, timeout.Token);
                    else
                        await ApplyAsync(state, e with { Cancelled = true }, prefs, timeout.Token);
                }
                page = result.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                nextSync = result.TryGetProperty("nextSyncToken", out var sync) ? sync.GetString() : nextSync;
            } while (page is not null);
            if (snapshot)
            {
                // Only a fully consumed successful snapshot may withdraw missing instances.
                var tracked = await store.Preparations.Find(x => x.OwnerAccountId == state.OwnerAccountId
                    && x.GoogleAccountId == state.GoogleAccountId && x.CalendarId == state.CalendarId
                    && x.Status != "Cancelled" && x.EndsAt > now).ToListAsync(timeout.Token);
                foreach (var missing in tracked.Where(x => !seen.Contains(x.CalendarEventId)))
                    await ApplyAsync(state, new(missing.CalendarEventId, missing.RecurringEventId, missing.Title,
                        missing.Description, missing.StartsAt, missing.EndsAt, missing.Timezone, missing.Attendees, true, false, false), prefs, timeout.Token);
                state.LastSnapshotAt = now;
            }
            state.SyncToken = nextSync; state.LastSuccessAt = DateTime.UtcNow; state.Attempts = 0; state.FailureCode = null;
            state.NextAttemptAt = DateTime.UtcNow.AddSeconds(options.Value.PollSeconds);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            state.Attempts++; state.FailureCode = e is AppException app ? app.Code : "calendar_sync_unavailable";
            // Expired sync cursors must bootstrap again. A provider 410 is not a successful empty calendar.
            state.SyncToken = null; // MCP may wrap provider 410; safe bounded re-bootstrap on the next attempt.
            state.NextAttemptAt = now.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(7, state.Attempts))));
            logger.LogWarning("CalendarSyncFailed {SyncId} {FailureCode}", state.Id, state.FailureCode);
        }
        finally
        {
            var lease = state.LeaseId; state.LeaseId = null; state.LeaseUntil = DateTime.MinValue;
            await states.ReplaceOneAsync(x => x.Id == state.Id && x.LeaseId == lease, state, cancellationToken: ct);
        }
    }
}
