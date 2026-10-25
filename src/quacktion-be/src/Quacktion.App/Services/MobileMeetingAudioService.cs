using System.Security.Cryptography;
using System.Text;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;

namespace Quacktion.App.Services;

public sealed class MobileMeetingAudioService(IMeetingRepo meetings, IMeetingSegmentRepo segments,
    IMeetingAudioStore audio, IMeetingTranscriber transcriber)
{
    private const int MaxBytes = 960000; // 30 seconds of signed 16 kHz mono PCM.

    public async Task<MeetingSegment> UploadAsync(string owner, string meetingId, long startMs, Stream body, CancellationToken ct)
    {
        Guard.Input(startMs >= 0, "invalid_segment");
        var meeting = Guard.Found(await meetings.FindAsync(m => m.Id == meetingId && m.OwnerAccountId == owner, ct));
        Guard.Input(meeting.DeviceId == "", "mobile_meeting_required");

        using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        int count;
        while ((count = await body.ReadAsync(block.AsMemory(0, Math.Min(block.Length, MaxBytes + 1 - (int)buffer.Length)), ct)) > 0)
        {
            buffer.Write(block, 0, count);
            if (buffer.Length > MaxBytes) throw new AppException(413, "meeting_audio_too_large");
        }
        var pcm = buffer.ToArray();
        Guard.Input(pcm.Length > 0 && pcm.Length % 1280 == 0, "invalid_segment_audio");
        var segmentId = "mobile-" + startMs;
        var segment = new MeetingSegment
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(meetingId + ":" + segmentId))).ToLowerInvariant(),
            OwnerAccountId = owner, MeetingId = meetingId, DeviceId = "", SegmentId = segmentId,
            BootId = "mobile", StartUptimeMs = startMs, StartMs = startMs, ClockSource = "relative",
            Frames = pcm.Length / 1280, Bytes = pcm.Length, Reason = "mobile_upload",
            AudioHash = Convert.ToHexString(SHA256.HashData(pcm)), EndMs = startMs + pcm.Length / 32,
            FailureCode = "transcription_pending"
        };
        var existing = await segments.GetAsync(segment.Id, ct);
        if (existing is not null)
        {
            Guard.State(existing.MeetingId == meetingId && existing.AudioHash == segment.AudioHash,
                "segment_conflict");
            await audio.SaveSegmentAsync(existing, pcm, ct);
            return existing;
        }
        Guard.State(meeting.EndedAt is null && meeting.Status == MeetingStatus.Active, "meeting_ended");
        long nextStartMs = 0;
        for (var offset = 0; ; offset += 100)
        {
            var page = await segments.OrderedAsync(meetingId, offset, 100, ct);
            foreach (var previous in page)
            {
                Guard.State(previous.StartMs == nextStartMs, "segment_offset_mismatch");
                nextStartMs += previous.Bytes / 32;
            }
            if (page.Count < 100) break;
        }
        Guard.Input(startMs == nextStartMs, "segment_offset_mismatch");
        try { await segments.InsertAsync(segment, ct); }
        catch (AppException e) when (e.Code == "duplicate_key")
        {
            existing = Guard.Found(await segments.GetAsync(segment.Id, ct));
            Guard.State(existing.MeetingId == meetingId && existing.AudioHash == segment.AudioHash,
                "segment_conflict");
            await audio.SaveSegmentAsync(existing, pcm, ct);
            return existing;
        }
        try { await audio.SaveSegmentAsync(segment, pcm, ct); }
        catch
        {
            segment.FailureCode = "audio_spool_failed";
            await Guard.Save(segments, segment, ct);
            throw;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(75));
            segment.Transcript = await transcriber.TranscribeAsync(pcm, timeout.Token);
            segment.FailureCode = null;
            segment.IsFinal = true;
        }
        catch (Exception e) when (e is AppException or HttpRequestException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            segment.FailureCode = e is AppException app ? app.Code : "transcription_failed";
        }
        await Guard.Save(segments, segment, ct);
        return segment;
    }
}
