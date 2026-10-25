using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Microsoft.AspNetCore.Mvc;

namespace Quacktion.Web.Controllers;

public sealed record MeetingStartRequest([Required, MaxLength(200)] string Name, MeetingType Type = MeetingType.General,
    bool RecordingConsent = false, string? DeviceId = null);
public sealed record DeviceModeRequest([Required] string Mode);
public sealed record SpeakerCorrectionRequest([Required, MaxLength(128)] string SpeakerId,
    [MaxLength(200)] string? SpeakerName);
public sealed record SpeakerNameRequest([MaxLength(200)] string? SpeakerName);

[Route("api/meetings")]
public sealed class MeetingController(MeetingService service, IMeetingRepo meetings, IMeetingSegmentRepo segments,
    IMeetingEventRepo events, IMeetingAnalysisRepo analyses, IMeetingInsightRepo insights,
    IMeetingTranscriptTurnRepo turns, IMeetingAudioStore audio,
    MeetingDeletionService deletion, MobileMeetingAudioService mobileAudio) : OwnedController
{
    [HttpPost("/api/devices/{deviceId}/meetings")]
    public async Task<IActionResult> Start(string deviceId, MeetingStartRequest request, CancellationToken ct) =>
        Ok(await service.StartAsync(Owner, deviceId, request.Name, ct, request.Type, request.RecordingConsent));

    [HttpPost]
    [HttpPost("start")]
    public async Task<IActionResult> StartMeeting(MeetingStartRequest request, CancellationToken ct)
    {
        var authenticatedDeviceId = User.FindFirstValue("device_id");
        if (authenticatedDeviceId is not null && request.DeviceId is not null && request.DeviceId != authenticatedDeviceId)
            throw new AppException(403, "device_id_mismatch");
        var deviceId = authenticatedDeviceId ?? request.DeviceId;
        if (deviceId is null)
            return Ok(await service.StartMobileAsync(Owner, request.Name, ct, request.Type, request.RecordingConsent));
        return Ok(await service.StartAsync(Owner, deviceId, request.Name, ct, request.Type, request.RecordingConsent));
    }

    [HttpPost("{id}/audio/segments")]
    public async Task<IActionResult> UploadMobileAudio(string id, [FromQuery] long? startMs, CancellationToken ct)
    {
        if (User.FindFirstValue("device_id") is not null) throw new AppException(403, "bearer_token_required");
        if (Request.ContentType != "application/octet-stream") throw new AppException(415, "unsupported_audio_format");
        Guard.Input(startMs is not null, "segment_offset_required");
        var segment = await mobileAudio.UploadAsync(Owner, id, startMs!.Value, Request.Body, ct);
        return Ok(segment);
    }

    [HttpPost("/api/devices/{deviceId}/mode")]
    public async Task<IActionResult> Mode(string deviceId, DeviceModeRequest request, CancellationToken ct)
    {
        await service.RequestModeAsync(Owner, deviceId, request.Mode, ct);
        return Accepted(new { requestedMode = request.Mode });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, int offset = 0, int limit = 50)
    {
        Guard.Page(offset, limit);
        return Ok(new Page<Meeting>(await meetings.ListAsync(m => m.OwnerAccountId == Owner, offset, limit, ct), offset, limit));
    }

    [HttpGet("{id}")] public async Task<IActionResult> Get(string id, CancellationToken ct) => Ok(await service.GetAsync(Owner, id, ct));
    [HttpPost("{id}/end")] public async Task<IActionResult> End(string id, CancellationToken ct) => Ok(await service.EndAsync(Owner, id, ct));
    [HttpGet("{id}/status")]
    public async Task<IActionResult> Status(string id, CancellationToken ct)
    {
        var meeting = await service.GetAsync(Owner, id, ct);
        return Ok(new { meeting.Id, meeting.Status, meeting.RecordingStatus, meeting.RecordingBytes,
            meeting.FinalTranscriptStatus, meeting.AnalysisFailureCode, meeting.ProcessedAt });
    }

    [HttpGet("{id}/transcript")]
    public async Task<IActionResult> Transcript(string id, CancellationToken ct, int offset = 0, int limit = 50)
    {
        Guard.Page(offset, limit);
        var meeting = await service.GetAsync(Owner, id, ct);
        if (meeting.FinalTranscriptStatus == "Ready")
            return Ok(new Page<MeetingTranscriptTurn>(await turns.OrderedAsync(id, offset, limit, ct), offset, limit));
        return Ok(new Page<MeetingSegment>(await segments.OrderedAsync(id, offset, limit, ct), offset, limit));
    }

    [HttpGet("{id}/audio")]
    public async Task<IActionResult> Audio(string id, CancellationToken ct)
    {
        var meeting = await service.GetAsync(Owner, id, ct);
        Guard.State(meeting.RecordingStatus == "Ready", "meeting_recording_not_ready");
        var stream = await audio.OpenFinalAsync(id, ct);
        return stream is null ? NotFound() : File(stream, "audio/wav", "meeting-" + id + ".wav");
    }

    [HttpGet("{id}/speakers")]
    public async Task<IActionResult> Speakers(string id, CancellationToken ct)
    {
        var meeting = await service.GetAsync(Owner, id, ct);
        var speakers = new Dictionary<string, string?>();
        if (meeting.FinalTranscriptStatus == "Ready")
        {
            for (var offset = 0; ; offset += 100)
            {
                var page = await turns.OrderedAsync(id, offset, 100, ct);
                foreach (var turn in page)
                    if (turn.SpeakerId is { } speakerId) speakers[speakerId] = turn.SpeakerName;
                if (page.Count < 100) break;
            }
            return Ok(speakers.Select(pair => new { speakerId = pair.Key, speakerName = pair.Value }));
        }
        for (var offset = 0; ; offset += 100)
        {
            var page = await segments.OrderedAsync(id, offset, 100, ct);
            foreach (var segment in page)
                if (segment.SpeakerId is { } speakerId) speakers[speakerId] = segment.SpeakerName;
            if (page.Count < 100) break;
        }
        return Ok(speakers.Select(pair => new { speakerId = pair.Key, speakerName = pair.Value }));
    }

    [HttpPatch("{id}/segments/{segmentId}/speaker")]
    public async Task<IActionResult> CorrectSegmentSpeaker(string id, string segmentId, SpeakerCorrectionRequest request, CancellationToken ct)
    {
        await service.GetAsync(Owner, id, ct);
        Guard.Input(!string.IsNullOrWhiteSpace(request.SpeakerId) && request.SpeakerId.Length <= 128, "invalid_speaker_id");
        var segment = Guard.Found(await segments.FindAsync(s => s.MeetingId == id && s.SegmentId == segmentId, ct));
        segment.SpeakerId = request.SpeakerId;
        segment.SpeakerName = request.SpeakerName;
        await Guard.Save(segments, segment, ct);
        return Ok(segment);
    }

    [HttpPatch("{id}/speakers/{speakerId}")]
    public async Task<IActionResult> CorrectSpeakerName(string id, string speakerId, SpeakerNameRequest request, CancellationToken ct)
    {
        var meeting = await service.GetAsync(Owner, id, ct);
        Guard.Input(!string.IsNullOrWhiteSpace(speakerId) && speakerId.Length <= 128, "invalid_speaker_id");
        var updated = 0;
        if (meeting.FinalTranscriptStatus == "Ready")
        {
            for (var offset = 0; ; offset += 100)
            {
                var page = await turns.OrderedAsync(id, offset, 100, ct);
                foreach (var turn in page.Where(t => t.SpeakerId == speakerId))
                {
                    turn.SpeakerName = request.SpeakerName;
                    await Guard.Save(turns, turn, ct);
                    updated++;
                }
                if (page.Count < 100) break;
            }
            Guard.State(updated > 0, "speaker_not_found");
            return Ok(new { speakerId, request.SpeakerName, updated });
        }
        for (var offset = 0; ; offset += 100)
        {
            var page = await segments.OrderedAsync(id, offset, 100, ct);
            foreach (var segment in page.Where(s => s.SpeakerId == speakerId))
            {
                segment.SpeakerName = request.SpeakerName;
                await Guard.Save(segments, segment, ct);
                updated++;
            }
            if (page.Count < 100) break;
        }
        Guard.State(updated > 0, "speaker_not_found");
        return Ok(new { speakerId, request.SpeakerName, updated });
    }

    [HttpGet("{id}/analysis")]
    public async Task<IActionResult> Analysis(string id, CancellationToken ct)
    {
        await service.GetAsync(Owner, id, ct);
        return Ok(Guard.Found(await analyses.FindAsync(a => a.MeetingId == id, ct)));
    }

    [HttpGet("{id}/insights")]
    public async Task<IActionResult> Insights(string id, CancellationToken ct, int offset = 0, int limit = 50)
    {
        Guard.Page(offset, limit);
        await service.GetAsync(Owner, id, ct);
        return Ok(new Page<MeetingInsight>(await insights.ListAsync(i => i.MeetingId == id, offset, limit, ct), offset, limit));
    }

    [HttpGet("{id}/scorecard")]
    public async Task<IActionResult> Scorecard(string id, CancellationToken ct)
    {
        await service.GetAsync(Owner, id, ct);
        return Ok(Guard.Found(await analyses.FindAsync(a => a.MeetingId == id, ct)).Scorecard);
    }

    [HttpGet("{id}/action-items")]
    public async Task<IActionResult> ActionItems(string id, CancellationToken ct)
    {
        await service.GetAsync(Owner, id, ct);
        return Ok(Guard.Found(await analyses.FindAsync(a => a.MeetingId == id, ct)).ActionItems);
    }

    [HttpGet("{id}/decisions")]
    public async Task<IActionResult> Decisions(string id, CancellationToken ct)
    {
        await service.GetAsync(Owner, id, ct);
        return Ok(Guard.Found(await analyses.FindAsync(a => a.MeetingId == id, ct)).Decisions);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var meeting = await service.GetAsync(Owner, id, ct);
        await deletion.DeleteAsync(meeting, ct);
        return NoContent();
    }

    [HttpGet("{id}/segments")]
    public async Task<IActionResult> Segments(string id, CancellationToken ct, int offset = 0, int limit = 50)
    {
        Guard.Page(offset, limit);
        await service.GetAsync(Owner, id, ct);
        return Ok(new Page<MeetingSegment>(await segments.OrderedAsync(id, offset, limit, ct), offset, limit));
    }

    [HttpGet("{id}/events")]
    public async Task<IActionResult> Events(string id, CancellationToken ct, int offset = 0, int limit = 50)
    {
        Guard.Page(offset, limit);
        await service.GetAsync(Owner, id, ct);
        return Ok(new Page<MeetingEvent>(await events.ListAsync(e => e.MeetingId == id, offset, limit, ct), offset, limit));
    }
}
