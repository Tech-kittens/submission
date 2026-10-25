using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public interface IPreparationDelivery
{
    Task DeliverAsync(MeetingPreparation preparation, PreparationEvent message, CancellationToken ct);
}
public static class BriefingPolicy
{
    public static bool Eligible(MeetingPreparation p, PreparationPreferences preferences, DateTime now) =>
        preferences.Enabled && preferences.MessagingOptIn && p.Status != "Cancelled"
        && p.BriefingStatus == "Pending" && p.BriefingDueAt <= now && p.StartsAt > now;
    public static DateTime RetryAt(DateTime now, int attempts) => now.AddSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(8, attempts))));
}
public sealed class PreparationDeliveryWorker(PreparationStore store, IPreparationDelivery delivery,
    ILogger<PreparationDeliveryWorker> logger)
{
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var p = await store.Preparations.FindOneAndUpdateAsync(x => x.LeaseUntil < now &&
            ((x.BriefingStatus == "Pending" && x.BriefingDueAt <= now)
                || x.Outbox.Any(e => e.Status == "Pending" && e.NextAttemptAt <= now)),
            Builders<MeetingPreparation>.Update.Set(x => x.LeaseId, Guid.NewGuid().ToString("N"))
                .Set(x => x.LeaseUntil, now.AddMinutes(2)).Inc(x => x.Version, 1),
            new FindOneAndUpdateOptions<MeetingPreparation> { ReturnDocument = ReturnDocument.After }, ct);
        if (p is null) return;
        var lease = p.LeaseId;
        try
        {
            var prefs = await store.PreferencesAsync(p.OwnerAccountId, ct);
            if (!await store.OwnerActiveAsync(p.OwnerAccountId, ct)) prefs.Enabled = false;
            if (p.StartsAt <= now || p.Status == "Cancelled") p.BriefingStatus = "Cancelled";
            if (BriefingPolicy.Eligible(p, prefs, now))
            {
                PreparationStore.Event(p, "meeting.briefing_requested"); p.BriefingStatus = "Queued";
            }
            if (!prefs.Enabled || !prefs.MessagingOptIn) p.BriefingStatus = "Suppressed";
            if (!await store.SaveAsync(p, ct)) return;
            var next = p.Outbox.FirstOrDefault(x => x.Status == "Pending" && x.NextAttemptAt <= now);
            if (next is null) return;
            var message = next.Type is "meeting.briefing_requested" or "meeting.clarification_requested";
            if (next.CalendarRevision != p.CalendarRevision || (!prefs.Enabled && next.Type != "meeting.cancelled")
                || (message && (!prefs.MessagingOptIn || p.StartsAt <= now || p.Status == "Cancelled"))
                || (next.Type == "meeting.clarification_requested" && !prefs.ClarificationsEnabled))
                next.Status = "Cancelled";
            else
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    // Recheck the version before external delivery. The receiver must also reject stale revisions.
                    var current = await store.GetAsync(p.OwnerAccountId, p.Id, timeout.Token);
                    if (current.Version != p.Version || current.LeaseId != lease) return;
                    await delivery.DeliverAsync(p, next, timeout.Token);
                    next.Status = "Delivered";
                    logger.LogInformation("NativePreparationDelivered {EventId} {MeetingId} {EventType}", next.Id, p.Id, next.Type);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    next.Attempts++; next.FailureCode = e is AppException app ? app.Code : "native_preparation_unavailable";
                    next.NextAttemptAt = BriefingPolicy.RetryAt(now, next.Attempts);
                    if (next.Attempts >= 10) { next.Status = "DeadLetter"; if (next.Type == "meeting.briefing_requested") p.BriefingStatus = "Failed"; }
                    logger.LogWarning("NativePreparationFailed {EventId} {Attempt} {FailureCode}", next.Id, next.Attempts, next.FailureCode);
                }
            }
            await store.SaveAsync(p, ct);
        }
        finally
        {
            await store.Preparations.UpdateOneAsync(x => x.Id == p.Id && x.LeaseId == lease,
                Builders<MeetingPreparation>.Update.Set(x => x.LeaseId, null).Set(x => x.LeaseUntil, DateTime.MinValue), cancellationToken: ct);
        }
    }
}
