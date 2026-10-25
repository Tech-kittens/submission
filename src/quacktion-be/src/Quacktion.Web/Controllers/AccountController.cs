using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;

namespace Quacktion.Web.Controllers;

[Route("api/accounts")]
public sealed class AccountController(IAccountService service) : OwnedController
{
    [HttpPost, AllowAnonymous, EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Create([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var account = await service.RegisterAsync(request, ct);
        return Created("/api/accounts/me", account);
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct) =>
        Ok(await service.GetAsync(Owner, ct));

    [HttpPatch("me")]
    public async Task<IActionResult> Update(ProfileRequest request, CancellationToken ct) =>
        Ok(await service.UpdateAsync(Owner, request, ct));

    [HttpDelete("me")]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        await service.DeleteAsync(Owner, ct);
        return NoContent();
    }

    [HttpPut("me/password"), EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Password(PasswordRequest request, CancellationToken ct)
    {
        await service.ChangePasswordAsync(Owner, request, ct);
        return NoContent();
    }
}
