using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
namespace Quacktion.Web.Controllers;

[Route("api/quacktions")]
public sealed class QuacktionController(IQuacktionService service, IApprovalService approvals) : OwnedController
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct, int offset = 0, int limit = 50) => Ok(new Page<App.Models.Quacktion>(await service.ListAsync(Owner, null, offset, limit, ct), offset, limit));
    [HttpGet("{id}")] public async Task<IActionResult> Get(string id, CancellationToken ct) => Ok(await service.GetAsync(Owner, id, ct));
    [HttpPost("{id}/approve")] public async Task<IActionResult> Approve(string id, ApprovalDecision request, CancellationToken ct) => Accepted($"/api/quacktions/{id}", await approvals.ResolveAsync(Owner, id, request.ActionDigest, true, ct));
    [HttpPost("{id}/reject")] public async Task<IActionResult> Reject(string id, ApprovalDecision request, CancellationToken ct) => Ok(await approvals.ResolveAsync(Owner, id, request.ActionDigest, false, ct));
    [HttpPost("{id}/cancel")] public async Task<IActionResult> Cancel(string id, CancellationToken ct) => Accepted($"/api/quacktions/{id}", await service.CancelAsync(Owner, id, ct));
    [HttpPost("{id}/retry")] public async Task<IActionResult> Retry(string id, CancellationToken ct) => Accepted($"/api/quacktions/{id}", await service.RetryAsync(Owner, id, ct));
}
