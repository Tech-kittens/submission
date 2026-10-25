using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Audio;
using Quacktion.Data.Agents;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public sealed class DeviceNotificationService(IMongoDbContext db, DeviceConnectionManager connections,
    NotificationSpeech speech, ILogger<DeviceNotificationService> logger, GeminiLiveService? live = null) : IDeviceNotificationService
{
    public IMongoCollection<DeviceNotification> Notifications => db.Collection<DeviceNotification>();
    public async Task<NotificationResult> NotifyAsync(string deviceId, DeviceNotificationRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        NotificationProtocol.Validate(request);
        Guard.Found(await db.Collection<Device>().Find(x => x.Id == deviceId && x.OwnerAccountId == request.OwnerAccountId
            && x.CredentialHash != null).FirstOrDefaultAsync(ct));
        var notification = new DeviceNotification { Id = request.NotifyId, OwnerAccountId = request.OwnerAccountId,
            DeviceId = deviceId, Kind = request.Kind, SpokenMessage = request.SpokenMessage, MeetingId = request.MeetingId,
            QuacktionId = request.QuacktionId, QuacktionStatus = request.QuacktionStatus,
            CalendarRevision = request.CalendarRevision, DueAt = request.DueAt, ExpiresAt = request.ExpiresAt,
            RetainUntil = request.ExpiresAt.AddDays(30), ExpiresInSeconds = request.ExpiresInSeconds };
        try { await Notifications.InsertOneAsync(notification, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var prior = Guard.Found(await Notifications.Find(x => x.Id == request.NotifyId && x.OwnerAccountId == request.OwnerAccountId && x.DeviceId == deviceId).FirstOrDefaultAsync(ct));
            Guard.State(prior.Kind == request.Kind && prior.MeetingId == request.MeetingId && prior.CalendarRevision == request.CalendarRevision
                && prior.SpokenMessage == request.SpokenMessage, "notification_idempotency_conflict");
            return new(prior.Id, prior.Status);
        }
        return new(notification.Id, notification.Status);
    }
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await Notifications.UpdateManyAsync(x => x.ExpiresAt <= now && (x.Status == "Scheduled" || x.Status == "Due" || x.Status == "WaitingForDevice" || x.Status == "Sent"),
            Builders<DeviceNotification>.Update.Set(x => x.Status, "Expired").Set(x => x.OfferResolution, "Expired"), cancellationToken: ct);
        await Notifications.UpdateManyAsync(x => x.DueAt <= now && x.ExpiresAt > now && x.Status == "Scheduled",
            Builders<DeviceNotification>.Update.Set(x => x.Status, "Due"), cancellationToken: ct);
        await Notifications.UpdateManyAsync(x => x.Status == "Due" && x.ExpiresAt > now,
            Builders<DeviceNotification>.Update.Set(x => x.Status, "WaitingForDevice"), cancellationToken: ct);
        foreach (var id in connections.ConnectedDeviceIds)
        {
            var connection = connections.Get(id);
            if (connection is null || connection.Lifetime.IsCancellationRequested || connection.MeetingModeActive
                || connection.LastAssistantAudioAt > now.AddSeconds(-2)) continue;
            // One in-flight notification per authenticated connection leaves headroom in the firmware's four-item queue.
            if (await Notifications.Find(x => x.DeviceId == id && x.Status == "Sent" && x.ConnectionId == connection.Id && x.NextAttemptAt > now).AnyAsync(ct)) continue;
            var lease = Guid.NewGuid().ToString("N");
            var n = await Notifications.FindOneAndUpdateAsync(x => x.DeviceId == id && x.ExpiresAt > now && x.DueAt <= now
                && x.LeaseUntil < now && x.Attempts < 5
                && ((x.Status == "WaitingForDevice" && x.NextAttemptAt <= now)
                    || (x.Status == "Sent" && (x.NextAttemptAt <= now || x.ConnectionId != connection.Id))),
                Builders<DeviceNotification>.Update.Set(x => x.LeaseId, lease).Set(x => x.LeaseUntil, now.AddSeconds(90)),
                new FindOneAndUpdateOptions<DeviceNotification> { ReturnDocument = ReturnDocument.After, Sort = Builders<DeviceNotification>.Sort.Ascending(x => x.DueAt) }, ct);
            if (n is null) continue;
            try
            {
                var device = await db.Collection<Device>().Find(x => x.Id == id && x.OwnerAccountId == n.OwnerAccountId && x.CredentialHash != null).FirstOrDefaultAsync(ct);
                if (device is null || !await db.Collection<Account>().Find(x => x.Id == n.OwnerAccountId && x.IsActive).AnyAsync(ct)
                    || !await RelevantAsync(n, ct))
                { await State(n, lease, "Cancelled", ct); continue; }
                byte[]? pcm = null;
                if (n.SpokenMessage is not null && !device.MutedByUser)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        pcm = await speech.GenerateAsync(n.SpokenMessage, timeout.Token);
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    { logger.LogWarning("NotificationSpeechUnavailable {NotificationId} {ErrorType}", n.Id, e.GetType().Name); }
                }
                // Calendar/preferences can change while TTS is running. Recheck immediately before enqueueing.
                if (!await RelevantAsync(n, ct)) { await State(n, lease, "Cancelled", ct); continue; }
                if (!connections.IsCurrent(id, connection) || connection.MeetingModeActive
                    || connection.LastAssistantAudioAt > DateTime.UtcNow.AddSeconds(-2)) continue;
                var expiry = Math.Min(n.ExpiresInSeconds, (int)(n.ExpiresAt - DateTime.UtcNow).TotalSeconds);
                if (expiry < 1) { await State(n, lease, "Expired", ct); continue; }
                var frames = NotificationProtocol.Frames(n.Id, n.Kind, expiry, pcm);
                // Record the attempt before sending: a crash can retry, but never loses the durable delivery intent.
                var sent = DateTime.UtcNow;
                var updated = await Notifications.UpdateOneAsync(x => x.Id == n.Id && x.LeaseId == lease
                    && (x.Status == "WaitingForDevice" || x.Status == "Sent"),
                    Builders<DeviceNotification>.Update.Set(x => x.Status, "Sent").Set(x => x.ConnectionId, connection.Id)
                        .Set(x => x.SentAt, sent).Set(x => x.Spoken, pcm is not null).Set(x => x.OfferSessionId, connection.AudioActive ? connection.SessionId : null)
                        .Inc(x => x.Attempts, 1).Set(x => x.NextAttemptAt, sent.AddSeconds(expiry + 15)), cancellationToken: ct);
                if (updated.ModifiedCount == 0) continue;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Lifetime.Token);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await connection.Outgoing.Writer.WriteAsync(new([], WebSocketMessageType.Text, completion, Batch: frames), writeTimeout.Token);
                await completion.Task.WaitAsync(writeTimeout.Token);
                logger.LogInformation("DeviceNotificationSent {NotificationId} {DeviceId} {Kind}", n.Id, id, n.Kind);
            }
            finally
            {
                await Notifications.UpdateOneAsync(x => x.Id == n.Id && x.LeaseId == lease,
                    Builders<DeviceNotification>.Update.Set(x => x.LeaseId, null).Set(x => x.LeaseUntil, DateTime.MinValue), cancellationToken: ct);
            }
        }
        await Notifications.UpdateManyAsync(x => x.Attempts >= 5 && x.Status == "Sent" && x.NextAttemptAt <= now,
            Builders<DeviceNotification>.Update.Set(x => x.Status, "Failed"), cancellationToken: ct);
    }
    private Task State(DeviceNotification n, string lease, string state, CancellationToken ct) => Notifications.UpdateOneAsync(
        x => x.Id == n.Id && x.LeaseId == lease, Builders<DeviceNotification>.Update.Set(x => x.Status, state), cancellationToken: ct);
    public async Task<bool> RelevantAsync(DeviceNotification n, CancellationToken ct)
    {
        if (n.ExpiresAt <= DateTime.UtcNow) return false;
        if (n.QuacktionId is not null)
        {
            var task = await db.Collection<Quacktion.App.Models.Quacktion>().Find(x => x.Id == n.QuacktionId && x.OwnerAccountId == n.OwnerAccountId).FirstOrDefaultAsync(ct);
            if (task is null || task.Status.ToString() != n.QuacktionStatus) return false;
        }
        if (n.MeetingId is null) return true;
        var prefs = await db.Collection<PreparationPreferences>().Find(x => x.OwnerAccountId == n.OwnerAccountId).FirstOrDefaultAsync(ct);
        if (prefs is null || !prefs.Enabled || !prefs.MessagingOptIn) return false;
        return await db.Collection<MeetingPreparation>().Find(x => x.Id == n.MeetingId && x.OwnerAccountId == n.OwnerAccountId
            && x.CalendarRevision == n.CalendarRevision && x.Status == "Ready" && x.EndsAt > DateTime.UtcNow
            && x.BriefingStatus != "Suppressed" && x.BriefingStatus != "Cancelled" && x.BriefingDueAt <= DateTime.UtcNow).AnyAsync(ct);
    }
    public async Task AcknowledgeAsync(Device device, string connectionId, JsonElement root, CancellationToken ct)
    {
        Guard.Input(root.GetProperty("version").GetInt32() == 1, "unsupported_protocol_version");
        var payload = root.TryGetProperty("payload", out var p) ? p : root;
        var id = payload.GetProperty("notifyId").GetString(); var outcome = payload.GetProperty("outcome").GetString();
        Guard.Input(id is { Length: > 0 and <= 48 } && outcome is "played" or "silent" or "expired" or "dropped", "notification_ack_invalid");
        var n = await Notifications.Find(x => x.Id == id && x.OwnerAccountId == device.OwnerAccountId && x.DeviceId == device.Id
            && x.ConnectionId == connectionId && x.Status == "Sent").FirstOrDefaultAsync(ct);
        if (n is null) return; // Duplicate or acknowledgement from a superseded transport.
        var now = DateTime.UtcNow; var relevant = await RelevantAsync(n, ct);
        var terminal = outcome is "played" or "silent";
        var connection = connections.Get(device.Id);
        var currentDevice = await db.Collection<Device>().Find(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId
            && x.CredentialHash != null).FirstOrDefaultAsync(ct);
        var offer = outcome == "played" && n.Spoken && n.MeetingId is not null && relevant
            && currentDevice is { MutedByUser: false } && connection?.AudioActive == true && n.OfferSessionId == connection.SessionId;
        long? Number(string key) => payload.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? number : null;
        var status = terminal ? "Acknowledged" : relevant && n.Attempts < 5 ? "WaitingForDevice" : n.ExpiresAt <= now ? "Expired" : "Failed";
        var acknowledged = await Notifications.UpdateOneAsync(x => x.Id == n.Id && x.Status == "Sent" && x.ConnectionId == connectionId,
            Builders<DeviceNotification>.Update.Set(x => x.Status, status).Set(x => x.Outcome, outcome)
                .Set(x => x.AcknowledgedAt, now).Set(x => x.AtMs, Number("atMs")).Set(x => x.AtUptimeMs, Number("atUptimeMs"))
                .Set(x => x.ClockSource, payload.TryGetProperty("clockSource", out var clock) ? clock.GetString() : null)
                .Set(x => x.OfferExpiresAt, offer ? now.AddSeconds(45) : null)
                .Set(x => x.NextAttemptAt, now.AddSeconds(30)), cancellationToken: ct);
        if (acknowledged.ModifiedCount == 0) return;
        if (terminal && n.QuacktionId is not null && n.Kind != "needs_confirmation")
        {
            var task = await db.Collection<Quacktion.App.Models.Quacktion>().Find(x => x.Id == n.QuacktionId && x.OwnerAccountId == n.OwnerAccountId).FirstOrDefaultAsync(ct);
            if (task is not null)
                await db.Collection<Quacktion.App.Models.Quacktion>().UpdateManyAsync(x => x.OwnerAccountId == n.OwnerAccountId
                    && x.SessionId == task.SessionId && x.Status == QuacktionStatus.PendingApproval,
                    Builders<Quacktion.App.Models.Quacktion>.Update.Set(x => x.NotificationAt, DateTime.MinValue).Inc(x => x.NotificationGeneration, 1), cancellationToken: ct);
        }
        if (offer && connection is not null)
        {
            connection.PendingBriefingNotificationId = n.Id;
            if (live is not null && connection.SessionId is { } sessionId)
            {
                try { await live.SetBriefingOfferContextAsync(device.OwnerAccountId, sessionId, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                { logger.LogWarning("BriefingOfferContextDeferred {NotificationId} {ErrorType}", n.Id, e.GetType().Name); }
            }
        }
        if (terminal && n.MeetingId is not null)
            await db.Collection<MeetingPreparation>().UpdateOneAsync(x => x.Id == n.MeetingId && x.OwnerAccountId == n.OwnerAccountId && x.CalendarRevision == n.CalendarRevision,
                Builders<MeetingPreparation>.Update.Set(x => x.BriefingStatus, outcome == "played" ? "Delivered" : "AcknowledgedSilent")
                    .Set(x => x.BriefingDeliveredAt, now), cancellationToken: ct);
        logger.LogInformation("DeviceNotificationAcknowledged {NotificationId} {Outcome}", n.Id, outcome);
    }
}

public sealed class NativePreparationDelivery(IMongoDbContext db, IDeviceNotificationService notifications,
    IOptions<PreparationSettings> settings) : IPreparationDelivery
{
    public async Task DeliverAsync(MeetingPreparation preparation, PreparationEvent message, CancellationToken ct)
    {
        // Calendar events already persist Pending research state atomically; the research worker claims it separately.
        if (message.Type != "meeting.briefing_requested") return;
        Guard.State(preparation.Status == "Ready", "preparation_not_ready");
        var devices = await db.Collection<Device>().Find(x => x.OwnerAccountId == preparation.OwnerAccountId && x.CredentialHash != null).ToListAsync(ct);
        Guard.State(devices.Count > 0, "briefing_device_unavailable");
        foreach (var device in devices)
            await notifications.NotifyAsync(device.Id, new() { NotifyId = Secrets.Digest(message.Id, device.Id)[..40],
                OwnerAccountId = preparation.OwnerAccountId, MeetingId = preparation.Id, CalendarRevision = preparation.CalendarRevision,
                DueAt = preparation.BriefingDueAt, ExpiresAt = preparation.EndsAt,
                SpokenMessage = settings.Value.SpokenNotifications ? NotificationProtocol.BriefingMessage : null }, ct);
    }
}
