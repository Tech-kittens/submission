using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
namespace Quacktion.Web.Controllers;

[ApiController, Authorize]
public abstract class OwnedController : ControllerBase { protected string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)!; }
