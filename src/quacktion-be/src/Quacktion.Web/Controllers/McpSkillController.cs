using Microsoft.AspNetCore.Mvc;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;

namespace Quacktion.Web.Controllers;

[Route("api/mcp/skills")]
public sealed class McpSkillController(IMcpSkillRegistry registry, IMcpSkillExecutionRepo executions) : OwnedController
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await registry.GetAvailableSkillsAsync(Owner, ct));

    [HttpGet("{skillId}")]
    public async Task<IActionResult> Get(string skillId, CancellationToken ct)
    {
        var skills = await registry.GetAvailableSkillsAsync(Owner, ct);
        return skills.FirstOrDefault(x => x.Id == skillId) is { } skill ? Ok(skill) : NotFound();
    }

    [HttpGet("executions")]
    public async Task<IActionResult> Executions(CancellationToken ct, int offset = 0, int limit = 50)
    {
        Quacktion.App.Services.Guard.Page(offset, limit);
        var records = await executions.ListAsync(x => x.UserId == Owner, offset, limit, ct);
        return Ok(records.Select(x => new
        {
            x.Id, x.ParentTaskId, x.ConnectionId, x.SkillId, x.Status,
            x.ApprovalTaskId, x.ErrorCode, x.ResultSummary, x.CreatedAt, x.CompletedAt
        }));
    }
}
