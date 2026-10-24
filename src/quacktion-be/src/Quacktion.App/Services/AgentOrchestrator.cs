using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.App.Services;

public sealed class AgentOrchestrator(IEnumerable<IAgentGateway> gateways, IAgentRepo agents) : IAgentOrchestrator
{
    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
    {
        var configured = await agents.ListAsync(x => x.Enabled && x.Capabilities.Contains(request.ActionType), 0, 100, ct);
        foreach (var agent in configured)
        {
            var gateway = gateways.FirstOrDefault(x => x.Provider == agent.Provider && x.Supports(request.ActionType));
            if (gateway is not null) return await gateway.ExecuteAsync(request, ct);
        }
        return new(false, null, "agent_not_configured");
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
