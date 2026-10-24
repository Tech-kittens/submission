using System.Text.Json;
using Quacktion.App.DTOs;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Xunit;
namespace Quacktion.App.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task RegisterHashLoginAndDuplicateEmail()
    {
        var f = new Fixture(); var a = await f.AccountService.RegisterAsync(new("User@example.test", "a-secure-password", "U"), default);
        Assert.NotEqual("a-secure-password", a.PasswordHash);
        Assert.DoesNotContain("passwordHash", JsonSerializer.Serialize(a), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty((await f.AccountService.LoginAsync(new("user@example.test", "a-secure-password"), default)).RefreshToken);
        Assert.Equal(401, (await Assert.ThrowsAsync<AppException>(() => f.AccountService.LoginAsync(new("user@example.test", "bad"), default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<AppException>(() => f.AccountService.RegisterAsync(new("USER@example.test", "a-secure-password", "U"), default))).Status);
    }
    [Fact]
    public async Task RotationReuseRevokesTheWholeFamily()
    {
        var f = new Fixture(); await f.Seed(); var first = await f.AccountService.LoginAsync(new("user@example.test", "a-secure-password"), default);
        var second = await f.AccountService.RefreshAsync(first.RefreshToken, default); Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        await Assert.ThrowsAsync<AppException>(() => f.AccountService.RefreshAsync(first.RefreshToken, default));
        await Assert.ThrowsAsync<AppException>(() => f.AccountService.RefreshAsync(second.RefreshToken, default));
    }
    [Fact]
    public async Task ConcurrentRefreshHasOneWinnerAndRevokesFamily()
    {
        var f = new Fixture(); await f.Seed(); var token = await f.AccountService.LoginAsync(new("user@example.test", "a-secure-password"), default);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => { try { return await f.AccountService.RefreshAsync(token.RefreshToken, default); } catch (AppException) { return null; } }));
        Assert.Single(results, x => x is not null);
        await Assert.ThrowsAsync<AppException>(() => f.AccountService.RefreshAsync(results.Single(x => x is not null)!.RefreshToken, default));
    }
    [Fact]
    public async Task PasswordChangeLogoutAndDeletionRevokeCredentials()
    {
        var f = new Fixture(); var (a, _) = await f.Seed(); var login = await f.AccountService.LoginAsync(new(a.Email, "a-secure-password"), default);
        await f.AccountService.ChangePasswordAsync(a.Id, new("a-secure-password", "another-secure-password"), default);
        await Assert.ThrowsAsync<AppException>(() => f.AccountService.RefreshAsync(login.RefreshToken, default));
        var next = await f.AccountService.LoginAsync(new(a.Email, "another-secure-password"), default); await f.AccountService.LogoutAsync(next.RefreshToken, default);
        await Assert.ThrowsAsync<AppException>(() => f.AccountService.RefreshAsync(next.RefreshToken, default));
        await f.AccountService.DeleteAsync(a.Id, default); await Assert.ThrowsAsync<AppException>(() => f.AccountService.LoginAsync(new(a.Email, "another-secure-password"), default));
    }
    [Fact]
    public async Task PairingRequiresOwnerOneTimeChallengeAndRevokesCredential()
    {
        var f = new Fixture(); var (a, _) = await f.Seed();
        var result = JsonSerializer.SerializeToElement(await f.DeviceService.CreateAsync(a.Id, new("serial-1", "Desk"), default));
        var id = result.GetProperty("Device").GetProperty("Id").GetString()!; var pairing = result.GetProperty("Pairing");
        var request = new PairRequest(pairing.GetProperty("ChallengeId").GetString()!, pairing.GetProperty("Challenge").GetString()!);
        await Assert.ThrowsAsync<AppException>(() => f.DeviceService.PairAsync("another-owner", id, request, default));
        var paired = JsonSerializer.SerializeToElement(await f.DeviceService.PairAsync(a.Id, id, request, default));
        var credential = paired.GetProperty("Credential").GetString()!;
        var auth = new DeviceAuthenticationService(f.Devices, f.Accounts);
        Assert.NotNull(await auth.AuthenticateAsync(id, credential, default)); Assert.Null(await auth.AuthenticateAsync(id, new string('0', 64), default));
        await Assert.ThrowsAsync<AppException>(() => f.DeviceService.PairAsync(a.Id, id, request, default));
        await f.DeviceService.UnpairAsync(a.Id, id, false, default); Assert.Null(await auth.AuthenticateAsync(id, credential, default));
        await Assert.ThrowsAsync<AppException>(() => f.DeviceService.PairAsync(a.Id, id, request, default));
    }
    [Fact]
    public async Task SessionOwnershipLifecycleAndInitializationFailure()
    {
        var f = new Fixture(); var (a, s) = await f.Seed();
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.GetAsync("other", s.Id, default));
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.StartAsync(a.Id, s.Id, default));
        Assert.Equal(SessionStatus.Completed, (await f.SessionService.EndAsync(a.Id, s.Id, default)).Status);
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.EndAsync(a.Id, s.Id, default));
        var failed = await f.SessionService.CreateAsync(a.Id, new("Failure", null), default); f.Voice.Fail = true;
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.StartAsync(a.Id, failed.Id, default));
        Assert.Equal(SessionStatus.Failed, (await f.Sessions.GetAsync(failed.Id, default))!.Status);
    }
    [Fact]
    public async Task WakeWordStartIsIdempotentAndSubmitsInitialPromptOnce()
    {
        var f = new Fixture(); var (account, _) = await f.Seed();
        var request = new SessionRequest("Wake word session", null, "What is our sprint status?", "text");
        var starts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => f.SessionService.StartNewAsync(account.Id, request, "wake-001", default)));
        Assert.Equal(starts[0].Id, starts[1].Id);
        Assert.All(starts, session => Assert.Equal(SessionStatus.Active, session.Status));
        Assert.Equal(new[] { "What is our sprint status?" }, f.Voice.UserTexts);
        Assert.Single(await f.Sessions.ListAsync(x => x.StartRequestId == "wake-001", 0, 10, default));
    }
    [Fact]
    public async Task TranscriptEventsPersistOnceAndPublishAgentResponse()
    {
        var f = new Fixture(); var (_, session) = await f.Seed();
        var service = new TranscriptService(f.Transcripts, f.SessionService, f.Sessions, f.Events);
        var entry = new TranscriptEntry { SessionId = session.Id, SpeakerIdentifier = "agent", Text = "Sprint is on track.", Source = "elevenlabs", SourceEventId = "agent_response:conversation-1:event-7" };
        await service.AppendAsync(entry, default);
        await service.AppendAsync(entry, default);
        Assert.Single(await f.Transcripts.ListAsync(x => x.SessionId == session.Id, 0, 10, default));
        Assert.Equal(1, f.Events.Names.Count(x => x == "TranscriptReceived"));
        Assert.Equal(1, f.Events.Names.Count(x => x == "AgentResponseReceived"));
    }
    [Fact]
    public async Task DeviceAssociationRejectsForeignOrBusyDevices()
    {
        var f = new Fixture(); var (a, _) = await f.Seed(); var d = new Device { OwnerAccountId = a.Id, CredentialHash = "hash" }; await f.Devices.InsertAsync(d, default);
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.CreateAsync("other", new("Meeting", d.Id), default));
        var one = await f.SessionService.CreateAsync(a.Id, new("One", d.Id), default); var two = await f.SessionService.CreateAsync(a.Id, new("Two", d.Id), default);
        await f.SessionService.StartAsync(a.Id, one.Id, default);
        await Assert.ThrowsAsync<AppException>(() => f.SessionService.StartAsync(a.Id, two.Id, default));
        Assert.Equal(one.Id, (await f.Devices.GetAsync(d.Id, default))!.ActiveSessionId);
        await f.SessionService.EndAsync(a.Id, one.Id, default); Assert.Null((await f.Devices.GetAsync(d.Id, default))!.ActiveSessionId);
    }
    [Theory]
    [InlineData("internal.research", true)]
    [InlineData("internal.summary", true)]
    [InlineData("send.email", false)]
    [InlineData("unknown", false)]
    [InlineData("INTERNAL.RESEARCH", false)]
    public void DeterministicRisk(string action, bool safe) => Assert.Equal(safe, new ActionRiskEvaluator().IsSafe(action));
    [Fact]
    public async Task SafeActionAutomaticallyExecutesAndRecordsHistory()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("internal.summary", "{}"), default);
        Assert.Equal(QuacktionStatus.Queued, q.Status); Assert.True(await f.Executor.ExecuteNextAsync(default));
        var done = (await f.Actions.GetAsync(q.Id, default))!; Assert.Equal(QuacktionStatus.Completed, done.Status); Assert.Single(done.History); Assert.Equal(1, f.Agent.Calls);
    }
    [Fact]
    public async Task ApprovalRequiresOwnerDigestAndCannotRunBeforeApproval()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("email.send", "{\"to\":\"test\"}"), default);
        Assert.False(await f.Executor.ExecuteNextAsync(default));
        await Assert.ThrowsAsync<AppException>(() => f.Approvals.ResolveAsync("other", q.Id, q.ActionDigest, true, default));
        await Assert.ThrowsAsync<AppException>(() => f.Approvals.ResolveAsync(a.Id, q.Id, "wrong", true, default));
        Assert.Equal(QuacktionStatus.Queued, (await f.Approvals.ResolveAsync(a.Id, q.Id, q.ActionDigest, true, default)).Status);
        await f.Executor.ExecuteNextAsync(default); Assert.Equal(1, f.Agent.Calls);
    }
    [Fact]
    public async Task ChangedParametersInvalidateApprovalAtExecution()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("external.send", "{}"), default);
        q = await f.Approvals.ResolveAsync(a.Id, q.Id, q.ActionDigest, true, default); q.ParametersJson = "{\"changed\":true}"; await f.Actions.ReplaceAsync(q, default);
        await f.Executor.ExecuteNextAsync(default); Assert.Equal(0, f.Agent.Calls); Assert.Equal("approval_invalid", (await f.Actions.GetAsync(q.Id, default))!.FailureCode);
    }
    [Fact]
    public async Task RejectedActionCannotExecuteOrRetry()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("external.send", "{}"), default);
        await f.Approvals.ResolveAsync(a.Id, q.Id, q.ActionDigest, false, default); Assert.False(await f.Executor.ExecuteNextAsync(default));
        await Assert.ThrowsAsync<AppException>(() => f.ActionService.RetryAsync(a.Id, q.Id, default));
    }
    [Fact]
    public async Task TransientFailuresBackoffThenStopWhilePermanentFailuresDoNotRetry()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("internal.analysis", "{}"), default);
        f.Agent.Run = (_, _) => Task.FromResult(new Interfaces.AgentTaskResult(false, null, "timeout", true));
        for (var i = 1; i <= 3; i++)
        {
            await f.Executor.ExecuteNextAsync(default); q = (await f.Actions.GetAsync(q.Id, default))!; Assert.Equal(i, q.Attempts);
            if (i < 3) { Assert.Equal(QuacktionStatus.Queued, q.Status); Assert.True(q.NextAttemptAt > DateTime.UtcNow); Assert.False(await f.Executor.ExecuteNextAsync(default)); q.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1); await f.Actions.ReplaceAsync(q, default); }
        }
        Assert.Equal(QuacktionStatus.Failed, q.Status);
        f.Agent.Run = (_, _) => Task.FromResult(new Interfaces.AgentTaskResult(false, null, "bad_request"));
        await f.ActionService.RetryAsync(a.Id, q.Id, default); await f.Executor.ExecuteNextAsync(default); Assert.Equal(QuacktionStatus.Failed, (await f.Actions.GetAsync(q.Id, default))!.Status);
    }
    [Fact]
    public async Task ConcurrentWorkersDoNotDuplicateAndSessionEndDoesNotCancelWork()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("internal.research", "{}"), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Agent.Run = async (_, ct) => { started.SetResult(); await release.Task.WaitAsync(ct); return new(true, "{}", null); };
        var execution = f.Executor.ExecuteNextAsync(default); await started.Task;
        Assert.False(await f.Executor.ExecuteNextAsync(default)); await f.SessionService.EndAsync(a.Id, s.Id, default);
        Assert.Equal(QuacktionStatus.InProgress, (await f.Actions.GetAsync(q.Id, default))!.Status);
        release.SetResult(); await execution; Assert.Equal(QuacktionStatus.Completed, (await f.Actions.GetAsync(q.Id, default))!.Status); Assert.Equal(1, f.Agent.Calls);
    }
    [Fact]
    public async Task CancellationDuringExecutionWinsOverAgentCompletion()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("internal.analysis", "{}"), default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Agent.Run = async (_, ct) => { await release.Task.WaitAsync(ct); return new(true, "{}", null); };
        var execution = f.Executor.ExecuteNextAsync(default); await f.ActionService.CancelAsync(a.Id, q.Id, default); release.SetResult(); await execution;
        Assert.Equal(QuacktionStatus.Cancelled, (await f.Actions.GetAsync(q.Id, default))!.Status);
    }
    [Fact]
    public async Task ExpiredLeaseRecoversWithStableIdempotencyKeyAndFencesOldWorker()
    {
        var f = new Fixture(); var (a, s) = await f.Seed(); var q = await f.ActionService.ProposeAsync(a.Id, s.Id, new("internal.analysis", "{}"), default);
        var old = (await f.Actions.ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(-1), default))!;
        var next = (await f.Actions.ClaimAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), default))!;
        Assert.Equal(q.IdempotencyKey, next.IdempotencyKey); Assert.NotEqual(old.LeaseId, next.LeaseId); Assert.False(await f.Actions.FinishAsync(old, old.LeaseId!, default));
    }
}
