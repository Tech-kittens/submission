using System.Text.Json;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Sessions;

public sealed class DeviceTransportPersistence(IMongoDbContext db) : IDeviceTransportPersistence
{
    private IMongoCollection<DeviceTransportState> States => db.Collection<DeviceTransportState>();
    public async Task AcquireAsync(Device device, string lease, string instanceId, CancellationToken ct)
    {
        try { await States.InsertOneAsync(new() { Id = device.Id, OwnerAccountId = device.OwnerAccountId }, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        var now = DateTime.UtcNow;
        var previous = await States.FindOneAndUpdateAsync(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && (x.LeaseExpiresAt <= now || x.InstanceId == instanceId),
            Builders<DeviceTransportState>.Update.Set(x => x.InstanceId, instanceId).Set(x => x.LeaseId, lease).Set(x => x.LeaseExpiresAt, now.AddSeconds(30)).Set(x => x.LastCheckpointAt, now),
            new() { ReturnDocument = ReturnDocument.Before }, ct);
        if (previous is null) throw new AppException(409, "device_connection_in_use");
        if (previous.LeaseId is not null && previous.GapId is null)
            await OpenGap(device, lease, previous.LastCheckpointAt, true, ct);
    }
    public async Task<bool> CheckpointAsync(Device device, string lease, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return (await States.UpdateOneAsync(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && x.LeaseId == lease && x.LeaseExpiresAt > now,
            Builders<DeviceTransportState>.Update.Set(x => x.LastCheckpointAt, now).Set(x => x.LeaseExpiresAt, now.AddSeconds(30)), cancellationToken: ct)).ModifiedCount == 1;
    }
    private async Task OpenGap(Device device, string lease, DateTime start, bool estimated, CancellationToken ct)
    {
        var current = await db.Collection<Device>().Find(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId).FirstOrDefaultAsync(ct);
        if (current?.MeetingId is not { } meetingId) return;
        var meeting = await db.Collection<Meeting>().Find(x => x.Id == meetingId && x.OwnerAccountId == device.OwnerAccountId && x.EndedAt == null).FirstOrDefaultAsync(ct);
        if (meeting is null) return;
        var gap = new MeetingEvent { Id = "gap_" + lease, OwnerAccountId = device.OwnerAccountId, DeviceId = device.Id, MeetingId = meetingId,
            Type = "recording_interruption", PayloadJson = JsonSerializer.Serialize(new { startedAt = start, endedAt = (DateTime?)null, startEstimated = estimated }) };
        try { await db.Collection<MeetingEvent>().InsertOneAsync(gap, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        await States.UpdateOneAsync(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && x.LeaseId == lease,
            Builders<DeviceTransportState>.Update.Set(x => x.GapId, gap.Id), cancellationToken: ct);
    }
    public async Task AudioReturnedAsync(Device device, string lease, CancellationToken ct)
    {
        var state = await States.Find(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && x.LeaseId == lease).FirstOrDefaultAsync(ct);
        if (state?.GapId is not { } id) return;
        var gap = await db.Collection<MeetingEvent>().Find(x => x.Id == id && x.OwnerAccountId == device.OwnerAccountId).FirstOrDefaultAsync(ct);
        if (gap is not null)
        {
            using var payload = JsonDocument.Parse(gap.PayloadJson);
            var start = payload.RootElement.GetProperty("startedAt").GetDateTime();
            var estimated = payload.RootElement.GetProperty("startEstimated").GetBoolean();
            await db.Collection<MeetingEvent>().UpdateOneAsync(x => x.Id == id && x.OwnerAccountId == device.OwnerAccountId,
                Builders<MeetingEvent>.Update.Set(x => x.PayloadJson, JsonSerializer.Serialize(new { startedAt = start, endedAt = DateTime.UtcNow, startEstimated = estimated })), cancellationToken: ct);
        }
        await States.UpdateOneAsync(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && x.LeaseId == lease && x.GapId == id,
            Builders<DeviceTransportState>.Update.Set(x => x.GapId, null), cancellationToken: ct);
    }
    public async Task DisconnectAsync(Device device, string lease, CancellationToken ct)
    {
        var state = await States.Find(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && x.LeaseId == lease).FirstOrDefaultAsync(ct);
        if (state is null) return;
        if (state.GapId is null) await OpenGap(device, lease, DateTime.UtcNow, false, ct);
        await States.UpdateOneAsync(x => x.Id == device.Id && x.OwnerAccountId == device.OwnerAccountId && x.LeaseId == lease,
            Builders<DeviceTransportState>.Update.Set(x => x.LeaseId, null).Set(x => x.LeaseExpiresAt, DateTime.MinValue), cancellationToken: ct);
    }
}
