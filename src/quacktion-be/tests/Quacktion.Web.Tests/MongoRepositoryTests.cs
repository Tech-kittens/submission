using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Repositories;
using Xunit;
using TaskAction = Quacktion.App.Models.Quacktion;
namespace Quacktion.Web.Tests;

public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO"))) Skip = "Set QUACKTION_TEST_MONGO to run against an isolated MongoDB database."; }
}
public sealed class MongoRepositoryTests
{
    [MongoFact]
    public async Task UniqueIndexesSecretsCasClaimsRenewalFencingAndAuditProjections()
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO")!;
        var name = "quacktion_test_" + Guid.NewGuid().ToString("N");
        var db = new MongoDbContext(Options.Create(new MongoDbSettings { ConnectionString = uri, Database = name }));
        try
        {
            await db.InitializeAsync(default);
            var accounts = new AccountRepo(db); var account = new Account { Email = "TEST@EXAMPLE.TEST", PasswordHash = "protected", SecurityStamp = "stamp" }; await accounts.InsertAsync(account, default);
            Assert.Equal("protected", (await accounts.GetAsync(account.Id, default))!.PasswordHash);
            Assert.Equal(409, (await Assert.ThrowsAsync<AppException>(() => accounts.InsertAsync(new() { Email = account.Email }, default))).Status);
            var devices = new DeviceRepo(db); var device = new Device { OwnerAccountId = account.Id, DeviceIdentifier = "esp-test", CredentialHash = "hash" }; await devices.InsertAsync(device, default);
            await Assert.ThrowsAsync<AppException>(() => devices.InsertAsync(new() { DeviceIdentifier = "esp-test" }, default));
            Assert.Equal("hash", (await devices.GetAsync(device.Id, default))!.CredentialHash);
            var stale = (await accounts.GetAsync(account.Id, default))!; account.Name = "first"; Assert.True(await accounts.ReplaceAsync(account, default)); stale.Name = "stale"; Assert.False(await accounts.ReplaceAsync(stale, default));
            var repo = new QuacktionRepo(db); var action = new TaskAction { OwnerAccountId = account.Id, Status = QuacktionStatus.Queued, ActionType = "internal.summary" }; await repo.InsertAsync(action, default);
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new QuacktionRepo(db).ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), default)));
            var claimed = Assert.Single(claims, x => x is not null)!; Assert.Equal(1, claimed.Attempts);
            Assert.True(await repo.RenewAsync(claimed.Id, claimed.LeaseId!, DateTime.UtcNow.AddSeconds(30), default));
            Assert.False(await repo.RenewAsync(claimed.Id, "wrong-lease", DateTime.UtcNow.AddSeconds(30), default));
            Assert.False(await repo.FinishAsync(claimed, "wrong-lease", default));
            var cancelling = (await repo.GetAsync(claimed.Id, default))!; cancelling.CancellationRequested = true; Assert.True(await repo.ReplaceAsync(cancelling, default));
            claimed.Status = QuacktionStatus.Completed; claimed.History[^1].FinishedAt = DateTime.UtcNow; Assert.True(await repo.FinishAsync(claimed, claimed.LeaseId!, default));
            var finished = (await repo.GetAsync(claimed.Id, default))!; Assert.Equal(QuacktionStatus.Cancelled, finished.Status);
            await repo.SyncAuditAsync(finished, default); Assert.Equal(1, await db.Collection<QuacktionExecution>().CountDocumentsAsync(x => x.QuacktionId == action.Id));
            var recovery = new TaskAction { Status = QuacktionStatus.Queued }; await repo.InsertAsync(recovery, default);
            var expired = (await repo.ClaimAsync(DateTime.UtcNow.AddMinutes(-1), TimeSpan.FromSeconds(1), default));
            // A fresh queued document is not eligible in the past; claim it now, then explicitly expire its lease.
            Assert.Null(expired);
            expired = (await repo.ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), default))!;
            expired.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1); await repo.ReplaceAsync(expired, default);
            var recovered = (await repo.ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), default))!;
            Assert.Equal(2, recovered.Attempts); Assert.Equal(expired.IdempotencyKey, recovered.IdempotencyKey); Assert.NotEqual(expired.LeaseId, recovered.LeaseId);
            Assert.False(await repo.FinishAsync(expired, expired.LeaseId!, default));
        }
        finally { await new MongoClient(uri).DropDatabaseAsync(name); }
    }
    [MongoFact]
    public async Task RealRefreshRotationRejectsReplay()
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO")!; var name = "quacktion_test_" + Guid.NewGuid().ToString("N");
        var db = new MongoDbContext(Options.Create(new MongoDbSettings { ConnectionString = uri, Database = name }));
        try
        {
            await db.InitializeAsync(default);
            var service = new AccountService(new AccountRepo(db), new RefreshTokenRepo(db), new Microsoft.AspNetCore.Identity.PasswordHasher<Account>(), new App.Tests.Issuer(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountService>.Instance);
            await service.RegisterAsync(new("test@example.test", "a-secure-password", "Test User"), default);
            var first = await service.LoginAsync(new("test@example.test", "a-secure-password"), default); var next = await service.RefreshAsync(first.RefreshToken, default);
            await Assert.ThrowsAsync<AppException>(() => service.RefreshAsync(first.RefreshToken, default)); await Assert.ThrowsAsync<AppException>(() => service.RefreshAsync(next.RefreshToken, default));
        }
        finally { await new MongoClient(uri).DropDatabaseAsync(name); }
    }
}
