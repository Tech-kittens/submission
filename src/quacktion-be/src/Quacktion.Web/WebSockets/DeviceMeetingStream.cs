using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Web.WebSockets;

// One instance per device connection. At most one 30-second PCM segment is held in memory.
public sealed class DeviceMeetingStream(IDeviceRepo devices, IMeetingRepo meetings, IMeetingSegmentRepo segments,
    IMeetingEventRepo events, IMeetingTranscriber transcriber, IMeetingAudioStore audioStore,
    MeetingService lifecycle, ISessionRepo sessions)
{
    private sealed record PendingTranscript(MeetingSegment Segment, byte[] Audio);
    private readonly Channel<PendingTranscript> pending = Channel.CreateBounded<PendingTranscript>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private Task? transcriptionWorker;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Device device = null!;
    private bool negotiated;
    private bool meetingModeObservedOnConnection;
    private MeetingSegment? segment;
    private MemoryStream? pcm;
    public bool UsesMeetingAudio => negotiated || device.Mode == "meeting";
    public bool MutedByUser => device.MutedByUser;
    public void Initialize(Device current) => device = current;
    public static bool Handles(string? type) => type is "wake" or "backlog.dropped" or "error" or "device.status"
        || type is not null && new[] { "mode.", "transcribe.", "segment.", "notify.", "confirm." }.Any(type.StartsWith);

    public async Task HandleAsync(JsonElement root, bool assistantAudio, Func<object, Task> send, CancellationToken ct)
    {
        var type = root.GetProperty("type").GetString();
        var body = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : root;
        if (root.TryGetProperty("version", out var version)) Guard.Input(version.GetInt32() == 1, "unsupported_protocol_version");
        switch (type)
        {
            case "mode.changed":
                var mode = String(body, "mode");
                Guard.Input(mode is "off" or "assistant" or "meeting", "invalid_mode");
                if (mode != "meeting") { await FinishAsync("mode", ct); negotiated = false; }
                // Only a device report can clear a physical mute. Remote start never clears it.
                await UpdateDevice(d =>
                {
                    d.Mode = mode!;
                    d.MutedByUser = mode == "off" && (d.MutedByUser || d.RequestedMode != "off"
                        || String(body, "reason") is "button" or "muted" || body.TryGetProperty("mutedByUser", out var muted) && muted.ValueKind == JsonValueKind.True);
                    if (d.RequestedMode == mode) d.RequestedMode = null;
                }, ct);
                if (mode == "meeting") meetingModeObservedOnConnection = true;
                if (mode != "off" && device.RequestedMode is { } requested)
                    await lifecycle.SetModeAsync(device.Id, requested, ct);
                else if (mode != "off" && device.MeetingId is { } id)
                {
                    var active = await meetings.GetAsync(id, ct);
                    if (active?.EndedAt is null && active is not null && mode != "meeting")
                    {
                        if (mode == "assistant" && meetingModeObservedOnConnection)
                            await lifecycle.EndAsync(device.OwnerAccountId, id, ct);
                        else
                            await lifecycle.SetModeAsync(device.Id, "meeting", ct);
                    }
                    else if (active?.EndedAt is not null && mode == "meeting")
                        await lifecycle.SetModeAsync(device.Id, "assistant", ct);
                }
                break;
            case "transcribe.start":
                Guard.Input(String(body, "format") == "pcm_16000" && Number(body, "frameMs") == 40
                    && Number(body, "preRollMs") is >= 0 and <= 1000
                    && Number(body, "hangoverMs") is >= 0 and <= 5000, "unsupported_audio_format");
                Guard.State(segment is null, "segment_in_progress");
                device = Guard.Found(await devices.GetAsync(device.Id, ct));
                Guard.State(!device.MutedByUser, "muted_by_user");
                Guard.State(device.Mode == "meeting", "invalid_mode");
                var meeting = await CurrentMeeting(ct);
                Guard.State(meeting.EndedAt is null, "meeting_ended");
                negotiated = true;
                await send(new { version = 1, type = "transcribe.ready", format = "pcm_16000", frameMs = 40, meetingId = meeting.Id });
                break;
            case "transcribe.stop":
                await FinishAsync("mode", ct); negotiated = false;
                break;
            case "segment.start":
                Guard.State(negotiated, "transcription_not_started");
                Guard.State(segment is null, "segment_in_progress");
                var start = body.Deserialize<SegmentStart>(Json) ?? throw new AppException(400, "invalid_segment");
                var split = start.SegmentId?.LastIndexOf('-') ?? -1;
                Guard.Input(start.SegmentId is { Length: <= 160 } && split > 0
                    && ulong.TryParse(start.SegmentId[(split + 1)..], out _)
                    && start.StartUptimeMs is >= 0 && start.PreRollMs is >= 0 and <= 1000
                    && start.ClockSource is { Length: > 0 and <= 32 }
                    && (start.ClockSource == "none" || start.StartMs is >= 0), "invalid_segment");
                Guard.Input(start.ContinuesSegmentId is null || (start.ContinuesSegmentId.Length <= 160
                    && start.ContinuesSegmentId != start.SegmentId
                    && start.ContinuesSegmentId.StartsWith(start.SegmentId![..(split + 1)], StringComparison.Ordinal)), "invalid_continuation");
                var current = await CurrentMeeting(ct);
                Guard.State(current.EndedAt is null && current.Status == MeetingStatus.Active, "meeting_ended");
                if (start.ContinuesSegmentId is { } parentId)
                {
                    var parent = await segments.FindAsync(s => s.DeviceId == device.Id && s.SegmentId == parentId, ct);
                    Guard.State(parent is null || parent.MeetingId == current.Id, "invalid_continuation");
                }
                segment = new MeetingSegment
                {
                    Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.Id + ":" + start.SegmentId))).ToLowerInvariant(),
                    MeetingId = current.Id, OwnerAccountId = device.OwnerAccountId, DeviceId = device.Id,
                    SegmentId = start.SegmentId!, BootId = start.SegmentId![..split], StartUptimeMs = start.StartUptimeMs!.Value,
                    StartMs = start.ClockSource == "none" ? null : start.StartMs, ClockSource = start.ClockSource!,
                    PreRollMs = start.PreRollMs, Backlog = start.Backlog, ContinuesSegmentId = start.ContinuesSegmentId
                };
                pcm = new MemoryStream();
                break;
            case "segment.end":
                Guard.State(segment is not null && pcm is not null, "segment_not_started");
                var reason = String(body, "reason");
                var valid = (String(body, "segmentId") is null || String(body, "segmentId") == segment!.SegmentId)
                    && Number(body, "frames") == pcm!.Length / 1280 && Number(body, "bytes") == pcm.Length
                    && reason is "silence" or "max_length" or "muted" or "mode" or "disconnect" or "overflow";
                if (!valid) { Reset(); throw new AppException(400, "segment_count_mismatch"); }
                await FinishAsync(reason!, ct);
                break;
            case "wake":
                Guard.Input(String(body, "outcome") is "session" or "ignored_busy"
                    && String(body, "segmentId") is { Length: > 0 and <= 160 }, "invalid_wake");
                if (String(body, "sessionId") is { } sessionId)
                    Guard.Found(await sessions.FindAsync(s => s.Id == sessionId && s.DeviceId == device.Id && s.OwnerAccountId == device.OwnerAccountId, ct));
                await RecordEvent(type!, body, ct);
                break;
            case "backlog.dropped":
                Guard.Input(String(body, "reason") is "overflow" or "muted", "invalid_backlog_report");
                await RecordEvent(type!, body, ct);
                break;
            case "error":
                if (String(body, "code") == "muted_by_user")
                    await UpdateDevice(d => { d.MutedByUser = true; d.Mode = "off"; }, ct);
                break;
            case "device.status":
                await UpdateDevice(d => d.LastSeenAt = DateTime.UtcNow, ct);
                break;
            default:
                await send(new { version = 1, type = "error", code = "unsupported_feature", messageType = type });
                break;
        }
    }

    public void Audio(byte[] frame)
    {
        Guard.State(negotiated && segment is not null && pcm is not null, "segment_not_started");
        if (frame.Length != 1280 || pcm!.Length + frame.Length > 960000)
        { Reset(); throw new AppException(400, "invalid_segment_audio"); }
        pcm.Write(frame);
    }

    public async Task FinishAsync(string reason, CancellationToken ct)
    {
        if (segment is null || pcm is null) return;
        var completed = segment;
        var bytes = pcm.ToArray();
        Reset();
        completed.Frames = bytes.Length / 1280;
        completed.Bytes = bytes.Length;
        completed.Reason = reason;
        completed.AudioHash = Convert.ToHexString(SHA256.HashData(bytes));
        for (var attempt = 0; ; attempt++)
        {
            Guard.State(attempt < 5, "segment_concurrency_conflict");
            var existing = await segments.GetAsync(completed.Id, ct);
            if (existing is not null)
            {
                Guard.State(existing.MeetingId == completed.MeetingId && existing.StartUptimeMs == completed.StartUptimeMs
                    && existing.ContinuesSegmentId == completed.ContinuesSegmentId && existing.StartMs == completed.StartMs
                    && existing.ClockSource == completed.ClockSource && existing.PreRollMs == completed.PreRollMs, "segment_conflict");
                if (existing.AudioHash == completed.AudioHash && (existing.FailureCode is null or "transcription_pending"))
                {
                    await audioStore.SaveSegmentAsync(existing, bytes, ct);
                    return;
                }
                // A replacement connection can finish a replay before the old connection saves its partial tail.
                if (reason == "disconnect" && existing.Bytes > completed.Bytes) return;
                Guard.State(existing.AudioHash == completed.AudioHash || existing.Reason == "disconnect" && existing.Bytes <= bytes.Length
                    && existing.AudioHash == Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, existing.Bytes))), "segment_conflict");
                completed.Version = existing.Version;
                completed.CreatedAt = existing.CreatedAt;
            }
            completed.FailureCode = "transcription_pending";
            if (existing is null)
            {
                try { await segments.InsertAsync(completed, ct); break; }
                catch (AppException e) when (e.Code == "duplicate_key") { }
            }
            else if (await segments.ReplaceAsync(completed, ct)) break;
        }
        try { await audioStore.SaveSegmentAsync(completed, bytes, ct); }
        catch
        {
            completed.FailureCode = "audio_spool_failed";
            await Guard.Save(segments, completed, ct);
            throw;
        }
        transcriptionWorker ??= ProcessTranscriptsAsync();
        if (!pending.Writer.TryWrite(new PendingTranscript(completed, bytes)))
        {
            completed.FailureCode = "transcription_queue_full";
            await Guard.Save(segments, completed, ct);
            throw new AppException(503, "transcription_queue_full");
        }
    }

    public async Task DrainAsync(CancellationToken ct)
    {
        pending.Writer.TryComplete();
        if (transcriptionWorker is not null) await transcriptionWorker.WaitAsync(ct);
    }

    private async Task ProcessTranscriptsAsync()
    {
        await foreach (var item in pending.Reader.ReadAllAsync())
        {
            var completed = item.Segment;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                completed.Transcript = item.Audio.Length == 0 ? "" : await transcriber.TranscribeAsync(item.Audio, timeout.Token);
                completed.FailureCode = null;
                completed.IsFinal = true;
                if (completed.StartMs is { } started) completed.EndMs = started + item.Audio.Length / 32;
            }
            catch (Exception e) when (e is AppException or HttpRequestException or OperationCanceledException or JsonException)
            { completed.FailureCode = e is AppException app ? app.Code : "transcription_failed"; }
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var current = await segments.GetAsync(completed.Id, saveTimeout.Token);
                    if (current is null || current.AudioHash != completed.AudioHash) break;
                    completed.Version = current.Version;
                    completed.CreatedAt = current.CreatedAt;
                    if (await segments.ReplaceAsync(completed, saveTimeout.Token)) break;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { await Task.Delay(TimeSpan.FromSeconds(attempt + 1)); }
            }
        }
    }

    private async Task<Meeting> CurrentMeeting(CancellationToken ct)
    {
        device = Guard.Found(await devices.GetAsync(device.Id, ct));
        var meeting = device.MeetingId is null ? null : await meetings.GetAsync(device.MeetingId, ct);
        if (meeting is null || meeting.OwnerAccountId != device.OwnerAccountId || meeting.DeviceId != device.Id)
            throw new AppException(409, "meeting_not_started");
        return meeting;
    }

    private async Task RecordEvent(string type, JsonElement payload, CancellationToken ct)
    {
        var meeting = await CurrentMeeting(ct);
        // A mute/loss report is metadata only. Never persist arbitrary fields that might contain audio.
        string[] allowed = type == "wake" ? ["segmentId", "outcome", "sessionId", "startMs", "startUptimeMs", "bootId"]
            : ["reason", "frames", "bytes", "segments", "durationMs", "bootId"];
        var metadata = payload.EnumerateObject().Where(p => allowed.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone());
        Guard.Input(metadata.Values.All(v => v.ValueKind == JsonValueKind.Null
            || v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 200
            || v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var number) && number >= 0), "invalid_event");
        await events.InsertAsync(new MeetingEvent { OwnerAccountId = device.OwnerAccountId, DeviceId = device.Id,
            MeetingId = meeting.Id, Type = type, PayloadJson = JsonSerializer.Serialize(metadata) }, ct);
    }

    private async Task UpdateDevice(Action<Device> update, CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            device = Guard.Found(await devices.GetAsync(device.Id, ct));
            update(device);
            if (await devices.ReplaceAsync(device, ct)) return;
        }
        throw new AppException(409, "concurrency_conflict");
    }

    private void Reset() { pcm?.Dispose(); pcm = null; segment = null; }
    private static string? String(JsonElement json, string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Number(JsonElement json, string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private sealed record SegmentStart(string? SegmentId, long? StartMs, long? StartUptimeMs, string? ClockSource,
        int PreRollMs = 0, bool Backlog = false, string? ContinuesSegmentId = null);
}
