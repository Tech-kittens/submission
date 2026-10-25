using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Services;
namespace Quacktion.Web.Controllers;

public sealed class SessionAudioSettings { public int ConnectionWindowSeconds { get; set; } = 60; }

[Route("api/sessions")]
public sealed class SessionController(ISessionService service, IDeviceAuthenticationService deviceAuthentication, ITranscriptService transcripts, IQuacktionService actions,
    IOptions<SessionAudioSettings> audioSettings, IWebHostEnvironment environment, IConversationPersistence history) : OwnedController
{
    [AllowAnonymous, HttpPost("start")]
    public async Task<IActionResult> StartAudioSession([FromBody] SessionAudioStartRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var authorization = Request.Headers.Authorization.ToString();
        string owner;
        string? deviceId = null;
        if (authorization.StartsWith("Device ", StringComparison.OrdinalIgnoreCase))
        {
            var device = await deviceAuthentication.AuthenticateCredentialAsync(authorization[7..], ct);
            if (device is null) throw new AppException(401, "invalid_device_credentials");
            owner = device.OwnerAccountId;
            deviceId = device.Id;
        }
        else if (User.Identity?.IsAuthenticated == true)
            owner = Owner;
        else
            throw new AppException(401, "invalid_session_authentication");

        var requestId = string.IsNullOrWhiteSpace(idempotencyKey) ? request.RequestId : idempotencyKey;
        var started = await service.CreateAudioSessionAsync(owner, deviceId, requestId ?? "", request.Name,
            audioSettings.Value.ConnectionWindowSeconds, ct);
        var scheme = Request.IsHttps || !environment.IsDevelopment() ? "wss" : "ws";
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
    [HttpPost("{id}/resume")] public async Task<IActionResult> Resume(string id, CancellationToken ct) => Ok(await service.SelectResumeAsync(Owner, id, ct));
    [HttpGet("{id}/history")] public async Task<IActionResult> History(string id, CancellationToken ct, long after = 0, int limit = 50) => Ok(await history.HistoryAsync(Owner, id, after, limit, ct));
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
