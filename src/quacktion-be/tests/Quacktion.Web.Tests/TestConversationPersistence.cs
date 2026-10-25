using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
namespace Quacktion.Web.Tests;
// API tests use in-memory repositories; Mongo lease semantics are tested separately against MongoDB.
internal sealed class TestConversationPersistence(ISessionRepo sessions) : IConversationPersistence
{
    private readonly Dictionary<string, string> leases = [];
    public async Task AcquireAsync(string owner, string session, string lease, CancellationToken ct)
    {
        if ((await sessions.GetAsync(session, ct))?.OwnerAccountId != owner) throw new AppException(404, "not_found");
        lock (leases) { if (!leases.TryAdd(session, lease)) throw new AppException(409, "conversation_in_use"); }
    }
    public Task<bool> RenewAsync(string owner, string session, string lease, CancellationToken ct) { lock (leases) return Task.FromResult(leases.GetValueOrDefault(session) == lease); }
    public Task ReleaseAsync(string owner, string session, string lease, CancellationToken ct) { lock (leases) { if (leases.GetValueOrDefault(session) == lease) leases.Remove(session); } return Task.CompletedTask; }
    public Task<bool> IsLeasedAsync(string owner, string session, CancellationToken ct) { lock (leases) return Task.FromResult(leases.ContainsKey(session)); }
    public Task RecordAsync(string owner, string session, string type, object payload, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<ConversationEvent>> HistoryAsync(string owner, string session, long after, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ConversationEvent>>([]);
}
