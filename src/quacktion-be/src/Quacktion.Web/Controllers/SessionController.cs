using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Services;
namespace Quacktion.Web.Controllers;

public sealed class SessionAudioSettings { public int ConnectionWindowSeconds { get; set; } = 60; }

[Route("api/sessions")]
public sealed class SessionController(ISessionService service, IDeviceRepo devices, ITranscriptService transcripts, IQuacktionService actions,
    IOptions<SessionAudioSettings> audioSettings) : OwnedController
{
    [HttpPost("start")]
    public async Task<IActionResult> StartAudioSession([FromBody] SessionAudioStartRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var pairedDevices = await devices.ListAsync(d => d.OwnerAccountId == Owner && d.CredentialHash != null, 0, 2, ct);
        if (pairedDevices.Count == 0) throw new AppException(409, "paired_device_required");
        if (pairedDevices.Count > 1) throw new AppException(409, "device_selection_required");
        var requestId = string.IsNullOrWhiteSpace(idempotencyKey) ? request.RequestId : idempotencyKey;
        var started = await service.CreateAudioSessionAsync(pairedDevices[0].Id, requestId ?? "", request.Name,
            audioSettings.Value.ConnectionWindowSeconds, ct);
        var scheme = Request.IsHttps ? "wss" : "ws";
        var webSocketUrl = $"{scheme}://{Request.Host}/ws/sessions/{Uri.EscapeDataString(started.Session.Id)}/audio";
        return Ok(new
        {
            sessionId = started.Session.Id,
            status = started.Session.Status.ToString(),
            webSocketUrl,
            expiresInSeconds = started.ExpiresInSeconds
        });
    }
    [HttpPost]
    public async Task<IActionResult> Create(SessionRequest request, [FromHeader(Name = "Idempotency-Key")] string? requestId, CancellationToken ct)
    {
        var s = request.InitialPrompt is not null || request.PromptFormat == "audio"
            ? await service.StartNewAsync(Owner, request, requestId ?? Guid.NewGuid().ToString("N"), ct)
            : await service.CreateAsync(Owner, request, ct);
        return CreatedAtAction(nameof(Get), new { id = s.Id }, s);
    }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct, int offset = 0, int limit = 50) => Ok(new Page<App.Models.Session>(await service.ListAsync(Owner, offset, limit, ct), offset, limit));
    [HttpGet("{id}")] public async Task<IActionResult> Get(string id, CancellationToken ct) => Ok(await service.GetAsync(Owner, id, ct));
    [HttpPatch("{id}")] public async Task<IActionResult> Update(string id, SessionUpdate request, CancellationToken ct) => Ok(await service.UpdateAsync(Owner, id, request, ct));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(string id, CancellationToken ct) { await service.DeleteAsync(Owner, id, ct); return NoContent(); }
    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id, [FromBody] SessionStartRequest? request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        Ok(await service.StartAsync(Owner, id, request?.InitialPrompt, request?.PromptFormat ?? "text", idempotencyKey ?? request?.RequestId, ct));
    [HttpPost("{id}/end")] public async Task<IActionResult> End(string id, [FromHeader(Name = "Idempotency-Key")] string? requestId, CancellationToken ct) => Ok(await service.EndAsync(Owner, id, ct, requestId));
    [HttpGet("{id}/transcript")] public async Task<IActionResult> Transcript(string id, CancellationToken ct, int offset = 0, int limit = 50) => Ok(new Page<App.Models.TranscriptEntry>(await transcripts.ListAsync(Owner, id, offset, limit, ct), offset, limit));
    [HttpGet("{id}/quacktions")] public async Task<IActionResult> Actions(string id, CancellationToken ct, int offset = 0, int limit = 50) { await service.GetAsync(Owner, id, ct); return Ok(new Page<App.Models.Quacktion>(await actions.ListAsync(Owner, id, offset, limit, ct), offset, limit)); }
    [HttpPost("{id}/quacktions")] public async Task<IActionResult> Propose(string id, ProposalRequest request, CancellationToken ct) { var q = await actions.ProposeAsync(Owner, id, request, ct); return Created($"/api/quacktions/{q.Id}", q); }
}
