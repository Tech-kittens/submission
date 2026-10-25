using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
namespace Quacktion.App.Services;

public sealed class AgentOrchestrator(IEnumerable<IAgentGateway> gateways, IAgentRepo agents, IAgentRouter router,
    ILogger<AgentOrchestrator>? logger = null) : IAgentOrchestrator
{
    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
    {
        if (request.ActionType == "mcp.invoke")
            return await ExecuteMeasuredAsync(gateways.Single(x => x.Provider == "mcp"), request, ct);
        var configured = await agents.ListAsync(x => x.Enabled && x.Capabilities.Contains(request.ActionType), 0, 100, ct);
        var available = configured
            .Select(agent => gateways.FirstOrDefault(x => (x.Provider == agent.Provider || agent.Provider == "gemini" && x.Provider == "argon4") && x.Supports(request.ActionType)))
            .Where(x => x is not null)
            .Cast<IAgentGateway>()
            .DistinctBy(x => x.Provider)
            .ToArray();
        if (configured.Count == 0)
        {
            available = gateways.Where(x => (x.Provider is "argon4" or "gemini") && x.Supports(request.ActionType))
                .DistinctBy(x => x.Provider).ToArray();
        }
        if (available.Length > 0)
        {
            var candidates = available.Select(g => new AgentRouteCandidate(g.Provider,
                configured.Count > 0
                    ? configured.Where(a => a.Provider == g.Provider).SelectMany(a => a.Capabilities).Distinct().ToArray()
                    : g.Provider == "argon4"
                        ? ["internal.preparation", "internal.analysis", "internal.research", "internal.mcp_read", "mcp.request"]
                        : ["internal.summary", "internal.suggestions"])).ToArray();
            try
            {
                var routedAt = Stopwatch.GetTimestamp();
                var route = await router.RouteAsync(new(request.ActionType, request.ParametersJson, candidates), ct);
                if (logger?.IsEnabled(LogLevel.Debug) == true)
                    logger.LogDebug("AgentTiming {SessionId} {Stage} {ElapsedMs:F2}", request.SessionId,
                        "jev_route", Stopwatch.GetElapsedTime(routedAt).TotalMilliseconds);
                var selected = route is { Confidence: >= 0.75 }
                    ? available.FirstOrDefault(g => g.Provider == route.Provider)
                    : null;
                if (selected is not null) return await ExecuteMeasuredAsync(selected, request, ct);
            }
            catch (HttpRequestException) { /* Deterministic allowlisted routing remains available during Jev outages. */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Jev timeout; preserve the durable task. */ }
            catch (TimeoutException) { /* A routing timeout must not fail the durable task. */ }
            catch (JsonException) { /* Invalid routing output falls back to deterministic routing. */ }
        }
        if (available.Length > 0)
        {
            var preferred = request.ActionType is "internal.preparation" or "internal.analysis" or "internal.research" or "internal.mcp_read" or "mcp.request" ? "argon4" : "gemini";
            var fallback = available.FirstOrDefault(x => x.Provider == preferred) ?? available[0];
            return await ExecuteMeasuredAsync(fallback, request, ct);
        }
        return new(false, null, "agent_not_configured");
    }

    private async Task<AgentTaskResult> ExecuteMeasuredAsync(IAgentGateway gateway, AgentTaskRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await gateway.ExecuteAsync(request, ct); }
        finally
        {
            if (logger?.IsEnabled(LogLevel.Debug) == true)
                logger.LogDebug("AgentTiming {SessionId} {Stage} {ElapsedMs:F2}", request.SessionId,
                    gateway.Provider == "argon4" ? "argon_execution" : "agent_execution",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
public sealed class ResearchService : IResearchService, IClaimVerificationService
{
    public Task<ResearchResult> ResearchAsync(string query, CancellationToken ct) => throw new AppException(503, "research_provider_not_configured");
    public Task<ClaimVerificationResult> VerifyAsync(string claim, CancellationToken ct) => Task.FromResult(new ClaimVerificationResult(VerificationAssessment.NotVerified, []));
}
public sealed class TranscriptService(ITranscriptRepo repo, ISessionService sessions, ISessionRepo sessionRepo, ISessionEvents events) : ITranscriptService
{
    public async Task<IReadOnlyList<TranscriptEntry>> ListAsync(string owner, string session, int offset, int limit, CancellationToken ct)
    { await sessions.GetAsync(owner, session, ct); Guard.Page(offset, limit); return await repo.ListAsync(x => x.SessionId == session, offset, limit, ct); }
    public async Task AppendAsync(TranscriptEntry entry, CancellationToken ct)
    {
        Guard.Input(entry.EndTime >= entry.StartTime && entry.StartTime >= 0 && entry.Text.Length is > 0 and <= 16000 && !string.IsNullOrWhiteSpace(entry.SourceEventId));
        var session = Guard.Found(await sessionRepo.GetAsync(entry.SessionId, ct));
        try { await repo.InsertAsync(entry, ct); }
        catch (AppException e) when (e.Code == "duplicate_key") { return; }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            session.UpdatedAt = DateTime.UtcNow;
            if (await sessionRepo.ReplaceAsync(session, ct)) break;
            session = Guard.Found(await sessionRepo.GetAsync(entry.SessionId, ct));
        }
        await events.PublishAsync(session.OwnerAccountId, session.Id, "TranscriptReceived", entry, ct);
        if (string.Equals(entry.SpeakerIdentifier, "agent", StringComparison.OrdinalIgnoreCase))
            await events.PublishAsync(session.OwnerAccountId, session.Id, "AgentResponseReceived", entry, ct);
    }
}
public sealed class AgentCatalog(IAgentRepo repo) : IAgentCatalog
{
    public Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct) => repo.ListAsync(x => x.Enabled, 0, 100, ct);
}
