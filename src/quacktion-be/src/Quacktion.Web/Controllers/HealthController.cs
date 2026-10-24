using Microsoft.AspNetCore.Mvc;
using Quacktion.Web.Infrastructure;
namespace Quacktion.Web.Controllers;

[ApiController, Route("health")]
public sealed class HealthController(DatabaseReadiness readiness) : ControllerBase
{
    [HttpGet("live")] public IActionResult Live() => Ok(new { Status = "alive" });
    [HttpGet("ready")] public IActionResult Ready() => readiness.Ready ? Ok(new { Status = "initialized" }) : StatusCode(503, new { Status = "initializing" });
}
