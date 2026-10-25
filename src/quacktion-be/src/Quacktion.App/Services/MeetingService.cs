using System.Text.Json;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;

namespace Quacktion.App.Services;

public sealed class MeetingService(IMeetingRepo meetings, IDeviceRepo devices, IDeviceConnectionManager connections,
    IMeetingEventRepo events)
{
    public async Task<Meeting> StartAsync(string owner, string deviceId, string name, CancellationToken ct,
        MeetingType type = MeetingType.General, bool recordingConsent = false)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(name) && name.Length <= 200, "invalid_meeting_name");
        Guard.Input(recordingConsent, "recording_consent_required");
        var device = Guard.Found(await devices.FindAsync(d => d.Id == deviceId && d.OwnerAccountId == owner, ct));
        Guard.State(device.CredentialHash is not null, "device_unpaired");
        Guard.State(!device.MutedByUser, "muted_by_user");
        Guard.State(connections.IsConnected(deviceId), "device_offline");
        var meeting = device.MeetingId is null ? null : await meetings.GetAsync(device.MeetingId, ct);
        if (meeting is null || meeting.EndedAt is not null)
        {
            meeting = new Meeting { OwnerAccountId = owner, DeviceId = deviceId, Name = name, Type = type,
                RecordingConsent = recordingConsent, RecordingEnabled = true, RecordingStatus = "Recording",
                Status = MeetingStatus.Active };
            await meetings.InsertAsync(meeting, ct);
            device.MeetingId = meeting.Id;
            if (!await devices.ReplaceAsync(device, ct))
            {
                await meetings.DeleteAsync(meeting, ct);
                throw new AppException(409, "concurrency_conflict");
            }
        }
        await AuditAsync(meeting, "meeting.started", ct);
        await SetModeAsync(deviceId, "meeting", ct);
        return meeting;
    }

    public async Task<Meeting> StartMobileAsync(string owner, string name, CancellationToken ct,
        MeetingType type = MeetingType.General, bool recordingConsent = false)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(name) && name.Length <= 200, "invalid_meeting_name");
        Guard.Input(recordingConsent, "recording_consent_required");
        var active = await meetings.FindAsync(m => m.OwnerAccountId == owner && m.DeviceId == ""
            && m.EndedAt == null && m.Status == MeetingStatus.Active, ct);
        if (active is not null) return active;
        var meeting = new Meeting { OwnerAccountId = owner, DeviceId = "", Name = name, Type = type,
            RecordingConsent = true, RecordingEnabled = true, RecordingStatus = "Recording",
            Status = MeetingStatus.Active };
        await meetings.InsertAsync(meeting, ct);
        await AuditAsync(meeting, "meeting.started", ct);
        return meeting;
    }

    public async Task<Meeting> GetAsync(string owner, string id, CancellationToken ct) =>
        Guard.Found(await meetings.FindAsync(m => m.Id == id && m.OwnerAccountId == owner, ct));

    public async Task<Meeting> EndAsync(string owner, string id, CancellationToken ct, string mode = "assistant")
    {
        var meeting = await GetAsync(owner, id, ct);
        if (meeting.EndedAt is not null) return meeting;
        meeting.EndedAt = DateTime.UtcNow;
        meeting.Status = MeetingStatus.Ending;
        await Guard.Save(meetings, meeting, ct);
        await AuditAsync(meeting, "meeting.ended", ct);
        // Retain the association until the next meeting so in-flight segments still belong to this meeting.
        var device = string.IsNullOrEmpty(meeting.DeviceId) ? null : await devices.GetAsync(meeting.DeviceId, ct);
        if (device?.MeetingId == id && !device.MutedByUser)
            await SetModeAsync(device.Id, mode, ct);
        return meeting;
    }

    public async Task RequestModeAsync(string owner, string deviceId, string mode, CancellationToken ct)
    {
        Guard.Input(mode is "off" or "assistant" or "meeting", "invalid_mode");
        var device = Guard.Found(await devices.FindAsync(d => d.Id == deviceId && d.OwnerAccountId == owner, ct));
        Guard.State(mode == "off" || !device.MutedByUser, "muted_by_user");
        if (mode == "meeting") throw new AppException(400, "recording_consent_required");
        if (device.MeetingId is { } id)
        {
            var meeting = await meetings.GetAsync(id, ct);
            if (meeting is { EndedAt: null }) { await EndAsync(owner, id, ct, mode); return; }
        }
        await SetModeAsync(deviceId, mode, ct);
    }

    public async Task SetModeAsync(string deviceId, string mode, CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var device = Guard.Found(await devices.GetAsync(deviceId, ct));
            Guard.State(mode == "off" || !device.MutedByUser, "muted_by_user");
            device.RequestedMode = mode;
            if (!await devices.ReplaceAsync(device, ct)) continue;
            await connections.SendTextAsync(deviceId, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "mode.set", mode }), ct);
            return;
        }
        throw new AppException(409, "concurrency_conflict");
    }

    private async Task AuditAsync(Meeting meeting, string type, CancellationToken ct)
    {
        var entry = new MeetingEvent
        {
            Id = Secrets.Digest(type, meeting.Id), OwnerAccountId = meeting.OwnerAccountId,
            DeviceId = meeting.DeviceId, MeetingId = meeting.Id, Type = type
        };
        try { await events.InsertAsync(entry, ct); }
        catch (AppException e) when (e.Code == "duplicate_key") { }
    }
}
