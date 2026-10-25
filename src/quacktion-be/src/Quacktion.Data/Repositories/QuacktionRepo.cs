using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.Data.MongoDB;
using TaskAction = Quacktion.App.Models.Quacktion;
namespace Quacktion.Data.Repositories;

public sealed class QuacktionRepo(IMongoDbContext db) : MongoRepo<TaskAction>(db), IQuacktionRepo
{
    public async Task<TaskAction?> ClaimAsync(DateTime now, TimeSpan lease, CancellationToken ct)
    {
        // CAS includes Version, so two workers cannot claim the same candidate. Expired leases are recoverable.
        var candidates = await Collection.Find(x => (x.Status == QuacktionStatus.Queued && x.NextAttemptAt <= now) || (x.Status == QuacktionStatus.InProgress && x.LeaseExpiresAt <= now))
            .SortBy(x => x.NextAttemptAt).Limit(20).ToListAsync(ct);
        foreach (var q in candidates)
        {
            if (q.Status == QuacktionStatus.InProgress && q.History.Count > 0)
            { q.History[^1].Outcome = "LeaseExpired"; q.History[^1].FinishedAt = now; }
            if (q.Attempts >= 5)
            { q.Status = QuacktionStatus.Failed; q.FailureCode = "attempt_limit"; if (await ReplaceAsync(q, ct)) await SyncAuditAsync(q, ct); continue; }
            q.Status = QuacktionStatus.InProgress; q.Attempts++; q.LeaseId = Guid.NewGuid().ToString("N"); q.LeaseExpiresAt = now + lease;
            q.History.Add(new() { QuacktionId = q.Id, LeaseId = q.LeaseId, Attempt = q.Attempts, CreatedAt = now });
            if (await ReplaceAsync(q, ct)) return q;
        }
        return null;
    }
    public async Task<bool> RenewAsync(string id, string leaseId, DateTime until, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var result = await Collection.UpdateOneAsync(x => x.Id == id && x.LeaseId == leaseId && x.Status == QuacktionStatus.InProgress && x.LeaseExpiresAt > now,
            Builders<TaskAction>.Update.Set(x => x.LeaseExpiresAt, until).Inc(x => x.Version, 1), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
    public async Task<bool> FinishAsync(TaskAction action, string leaseId, CancellationToken ct)
    {
        // Retry on cancellation/renewal races, always honoring the latest cancellation flag.
        for (var i = 0; i < 5; i++)
        {
            var current = await GetAsync(action.Id, ct);
            if (current is null || current.LeaseId != leaseId || current.Status != QuacktionStatus.InProgress || current.LeaseExpiresAt <= DateTime.UtcNow) return false;
            action.Version = current.Version;
            if (current.CancellationRequested) { action.CancellationRequested = true; action.Status = QuacktionStatus.Cancelled; action.History[^1].Outcome = "Cancelled"; }
            action.LeaseId = null; action.LeaseExpiresAt = null;
            if (await ReplaceAsync(action, ct)) return true;
        }
        return false;
    }
    public async Task SyncAuditAsync(TaskAction action, CancellationToken ct)
    {
        // Embedded history/approval is authoritative and committed atomically. Collections are repairable read projections.
        foreach (var execution in action.History)
            await Context.Collection<QuacktionExecution>().ReplaceOneAsync(x => x.Id == execution.Id, execution, new ReplaceOptions { IsUpsert = true }, ct);
        if (action.Approval is { } approval)
        {
            var collection = Context.Collection<ApprovalRequest>();
            var filter = Builders<ApprovalRequest>.Filter.Eq(x => x.QuacktionId, approval.QuacktionId);
            var update = Builders<ApprovalRequest>.Update
                .Set(x => x.OwnerAccountId, approval.OwnerAccountId)
                .Set(x => x.ActionDigest, approval.ActionDigest)
                .Set(x => x.Status, approval.Status)
                .Set(x => x.ExpiresAt, approval.ExpiresAt)
                .Set(x => x.ResolvedAt, approval.ResolvedAt)
                .Set(x => x.Version, approval.Version)
                .SetOnInsert(x => x.Id, approval.Id)
                .SetOnInsert(x => x.CreatedAt, approval.CreatedAt);
            try
            {
                await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Concurrent upserts may race on the unique QuacktionId index. Retry as an update
                // of the winner, preserving its existing Mongo _id.
                await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = false }, ct);
            }
        }
    }
}
