using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
namespace Quacktion.Web.Controllers;

[Route("api/devices")]
public sealed class DeviceController(IDeviceService service, IDeviceConnectionManager connections) : OwnedController
{
    [HttpPost] public async Task<IActionResult> Create(DeviceRequest request, CancellationToken ct) => StatusCode(201, await service.CreateAsync(Owner, request, ct));
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct, int offset = 0, int limit = 50) => Ok(new Page<App.Models.Device>(await service.ListAsync(Owner, offset, limit, ct), offset, limit));
    [HttpGet("{id}")] public async Task<IActionResult> Get(string id, CancellationToken ct) => Ok(await service.GetAsync(Owner, id, ct));
    [HttpPatch("{id}")] public async Task<IActionResult> Update(string id, DeviceUpdate request, CancellationToken ct) => Ok(await service.UpdateAsync(Owner, id, request, ct));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(string id, CancellationToken ct) { await service.UnpairAsync(Owner, id, true, ct); return NoContent(); }
    [HttpPost("{id}/pairing-challenges"), EnableRateLimiting("sensitive")] public async Task<IActionResult> Challenge(string id, CancellationToken ct) => Ok(await service.ChallengeAsync(Owner, id, ct));
    [HttpPost("{id}/pair"), EnableRateLimiting("sensitive")] public async Task<IActionResult> Pair(string id, PairRequest request, CancellationToken ct) => Ok(await service.PairAsync(Owner, id, request, ct));
    [HttpPost("{id}/unpair")] public async Task<IActionResult> Unpair(string id, CancellationToken ct) { await service.UnpairAsync(Owner, id, false, ct); return NoContent(); }
    [HttpGet("{id}/status")] public async Task<IActionResult> Status(string id, CancellationToken ct) { var d = await service.GetAsync(Owner, id, ct); return Ok(new { d.Id, d.Status, d.LastSeenAt, Connected = connections.IsConnected(id) }); }
}
