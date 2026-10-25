using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quacktion.App.Interfaces;

namespace Quacktion.Web.Controllers;

[Route("api/mcp")]
public sealed class McpController(IMcpConnectionService service) : OwnedController
{
    [HttpGet("providers")]
    public IActionResult Providers() => Ok(service.Providers());

    [HttpGet("connections")]
    public async Task<IActionResult> Connections(CancellationToken ct) => Ok(await service.ListAsync(Owner, ct));

    [HttpPost("connections/{serverId}/connect"), EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Connect(string serverId, CancellationToken ct) =>
        Ok(new { authorizationUrl = await service.BeginConnectAsync(Owner, serverId, ct) });

    [AllowAnonymous, HttpGet("oauth/callback")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error)) return BadRequest(new { error = "mcp_authorization_denied" });
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state)) return BadRequest(new { error = "invalid_oauth_callback" });
        await service.CompleteConnectAsync(code, state, ct);
        return Redirect("https://quackback.ofneill.com/mobile/mcp-complete");
    }

    [HttpDelete("connections/{connectionId}"), EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Disconnect(string connectionId, CancellationToken ct)
    { await service.DisconnectAsync(Owner, connectionId, ct); return NoContent(); }

    [HttpGet("connections/{connectionId}/tools")]
    public async Task<IActionResult> Tools(string connectionId, CancellationToken ct) => Ok(await service.ListToolsAsync(Owner, connectionId, ct));

    [HttpPost("connections/{connectionId}/tasks/{taskId}/select-account")]
    public async Task<IActionResult> SelectAccount(string connectionId, string taskId, McpTaskAccountSelection selection, CancellationToken ct) =>
        Ok(await service.BindTaskAsync(Owner, connectionId, taskId, selection.Services, ct));

    [HttpGet("connections/{connectionId}/health")]
    public async Task<IActionResult> Health(string connectionId, CancellationToken ct)
    { await service.HealthAsync(Owner, connectionId, ct); return Ok(new { status = "available" }); }

    [HttpPatch("connections/{connectionId}/tools")]
    public async Task<IActionResult> SetTools(string connectionId, McpToolSelection request, CancellationToken ct) =>
        Ok(await service.SetEnabledToolsAsync(Owner, connectionId, request.EnabledTools, ct));
}
