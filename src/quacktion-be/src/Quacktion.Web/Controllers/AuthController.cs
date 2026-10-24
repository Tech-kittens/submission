using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
namespace Quacktion.Web.Controllers;

[ApiController, Route("api/auth"), EnableRateLimiting("sensitive")]
public sealed class AuthController(IAccountService service) : ControllerBase
{
    [HttpPost("login")] public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct) => Ok(await service.LoginAsync(request, ct));
    [HttpPost("refresh")] public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct) => Ok(await service.RefreshAsync(request.RefreshToken, ct));
    [HttpPost("logout")] public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct) { await service.LogoutAsync(request.RefreshToken, ct); return NoContent(); }
}
