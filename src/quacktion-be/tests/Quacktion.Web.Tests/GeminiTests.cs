using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.App.Tests;
using Quacktion.Data.Agents;
using Quacktion.Data.Audio;
using Xunit;

namespace Quacktion.Web.Tests;

public sealed class GeminiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedCheckpointIsUsedAcrossRuntimeConnectionsWithRejectedHandleFallback(bool reject)
    {
        await using var f = await LiveFixture.Create();
        await f.States.InsertAsync(new() { Id = f.Session.Id, SessionId = f.Session.Id, OwnerAccountId = f.Account.Id,
            ResumptionHandle = "persisted-handle", HandleUpdatedAt = DateTime.UtcNow }, default);
        await f.Fixture.Transcripts.InsertAsync(new() { SessionId = f.Session.Id, Text = "Remember the security review", SourceEventId = "saved" }, default);
        var first = new FakeSocket(!reject);
        if (reject) first.Push(new { error = new { code = 400, message = "Expired handle" } });
        f.Factory.Sockets.Enqueue(first);
        var fresh = new FakeSocket(); if (reject) f.Factory.Sockets.Enqueue(fresh);
        await f.Start();
        Assert.Equal("persisted-handle", first.Sent.First().GetProperty("setup").GetProperty("sessionResumption").GetProperty("handle").GetString());
        if (reject)
        {
            Assert.Contains(fresh.Sent, x => x.TryGetProperty("clientContent", out _) && x.GetRawText().Contains("security review"));
            Assert.Null((await f.States.GetAsync(f.Session.Id, default))!.ResumptionHandle);
        }
    }

    [Fact]
    public void SetupUsesWireSchemaAndServerOnlyAuthentication()
    {
        var settings = new GeminiSettings { ApiKey = "secret+/=?" };
        var setup = JsonSerializer.SerializeToElement(GeminiProtocol.Setup(settings, "resume-handle"), GeminiProtocol.Json).GetProperty("setup");
        Assert.Equal("models/gemini-3.8-live", setup.GetProperty("model").GetString());
        Assert.False(setup.TryGetProperty("responseModalities", out _));
        Assert.Equal("AUDIO", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.Equal("Puck", setup.GetProperty("generationConfig").GetProperty("speechConfig").GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        Assert.Equal("resume-handle", setup.GetProperty("sessionResumption").GetProperty("handle").GetString());
        Assert.True(setup.TryGetProperty("contextWindowCompression", out _));
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
        var activity = setup.GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection");
        Assert.Equal(500, activity.GetProperty("silenceDurationMs").GetInt32());
        Assert.Equal("START_SENSITIVITY_LOW", activity.GetProperty("startOfSpeechSensitivity").GetString());
        Assert.False(setup.TryGetProperty("thinkingConfig", out _));
        Assert.False(setup.TryGetProperty("thinkingLevel", out _));
        Assert.DoesNotContain("proactiveAudio", setup.GetRawText());
        Assert.DoesNotContain(settings.ApiKey, setup.GetRawText());
        Assert.EndsWith("?key=secret%2B%2F%3D%3F", GeminiProtocol.AuthenticatedUri(settings.ApiKey).AbsoluteUri);
        Assert.All(setup.GetProperty("tools")[0].GetProperty("functionDeclarations").EnumerateArray(), d => Assert.Equal("NON_BLOCKING", d.GetProperty("behavior").GetString()));
        Assert.Contains("without confirmation from the backend", settings.SystemInstruction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A customised meeting persona.")]
    public void ConnectedAccountRoutingSurvivesPersonaOverrides(string? persona)
    {
        var settings = new GeminiSettings();
        if (persona is not null) settings.SystemInstruction = persona;
        var setup = JsonSerializer.SerializeToElement(GeminiProtocol.Setup(settings), GeminiProtocol.Json).GetProperty("setup");
        var parts = setup.GetProperty("systemInstruction").GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal(settings.SystemInstruction, parts[0].GetProperty("text").GetString());
        Assert.Contains("execute_quacktion", parts[1].GetProperty("text").GetString());
        Assert.Contains("execute_quacktion", setup.GetProperty("tools")[0].GetProperty("functionDeclarations")
            .EnumerateArray().Select(x => x.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(800)]
    public void VadCandidatesAreSentAsSupportedAutomaticActivityDetection(int silenceMs)
    {
        var settings = new GeminiSettings { SilenceDurationMs = silenceMs };
        var setup = JsonSerializer.SerializeToElement(GeminiProtocol.Setup(settings), GeminiProtocol.Json);
        var activity = setup.GetProperty("setup").GetProperty("realtimeInputConfig")
            .GetProperty("automaticActivityDetection");
        Assert.Equal(silenceMs, activity.GetProperty("silenceDurationMs").GetInt32());
        Assert.False(activity.GetProperty("disabled").GetBoolean());
        Assert.True(setup.GetProperty("setup").TryGetProperty("inputAudioTranscription", out _));
    }

    [Fact]
    public async Task SetupWaitsForAcknowledgementAndFailureCleansUpForRetry()
    {
        await using var f = await LiveFixture.Create();
        var blocked = new FakeSocket(false);
        f.Factory.Sockets.Enqueue(blocked);
        var start = f.Start();
        await Until(() => Task.FromResult(blocked.Sent.Count == 1));
        Assert.False(start.IsCompleted);
        blocked.Push(new { setupComplete = new { } });
        var result = await start;
        Assert.NotEqual(f.Session.Id, result.AgentSessionId);
        await f.Live.EndSessionAsync(result.AgentSessionId, default);
        Assert.True(blocked.Disposed);
        var bad = new FakeSocket(false);
        bad.CloseFromServer(WebSocketCloseStatus.InvalidPayloadData, "Unknown field including PRIVATE CONTENT");
        f.Factory.Sockets.Enqueue(bad);
        var error = await Assert.ThrowsAsync<AppException>(() => f.Start());
        Assert.Equal("gemini_invalid_setup", error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        f.Factory.Sockets.Enqueue(new());
        await f.Start();
    }

    [Fact]
    public async Task InputIsPcmBase64InThirtyMillisecondChunksAndOutputIsDecoded()
    {
        await using var f = await LiveFixture.Create();
        var socket = new FakeSocket(); f.Factory.Sockets.Enqueue(socket);
        var live = await f.Start();
        await f.Live.NegotiateAsync(live.AgentSessionId, new(1, new("pcm_16000", 16000, 1), new("pcm_24000", 24000, 1)), default);
        var input = Enumerable.Range(0, 3200).Select(i => (byte)i).ToArray();
        await f.Live.SendAudioAsync(live.AgentSessionId, input, default);
        var audio = socket.Sent.Where(x => x.TryGetProperty("realtimeInput", out _)).Select(x => x.GetProperty("realtimeInput").GetProperty("audio")).ToArray();
        Assert.Equal(4, audio.Length);
        Assert.All(audio, x => Assert.Equal("audio/pcm;rate=16000", x.GetProperty("mimeType").GetString()));
        Assert.Equal(input, audio.SelectMany(x => Convert.FromBase64String(x.GetProperty("data").GetString()!)).ToArray());
        socket.Push(Audio(input[..960]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = f.Live.ReadEventsAsync(live.AgentSessionId, timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(input[..960], reader.Current.Audio);
        Assert.False(string.IsNullOrWhiteSpace(reader.Current.TurnId));
        Assert.True(reader.Current.ReceivedAt > 0);
        await Assert.ThrowsAsync<AppException>(() => f.Live.SendAudioAsync(live.AgentSessionId, new byte[3], default));
    }

    [Fact]
    public async Task SlowToolJournalDoesNotBlockFollowingGeminiAudio()
    {
        await using var f = await LiveFixture.Create(callInsertDelay: TimeSpan.FromMilliseconds(800));
        var socket = new FakeSocket(); f.Factory.Sockets.Enqueue(socket);
        var live = await f.Start();
        await f.Live.NegotiateAsync(live.AgentSessionId,
            new(1, new("pcm_16000", 16000, 1), new("pcm_24000", 24000, 1)), default);
        socket.Push(new { toolCall = new { functionCalls = new[] {
            new { id = "slow-journal", name = "create_quacktion", args = new { actionType = "internal.summary", query = "Summary" } }
        } } });
        socket.Push(Audio(new byte[960]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await using var reader = f.Live.ReadEventsAsync(live.AgentSessionId, timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(960, reader.Current.Audio!.Length);
        await Until(async () => (await f.Calls.ListAsync(x => true, 0, 100, default)).Count == 1);
    }

    [Fact]
    public void ResamplingPreservesTimingChunkBoundariesAndRejectsAliases()
    {
        var input = Tone(1000, 24000);
        var complete = new PcmResampler().Convert(input);
        Assert.Equal(32000, complete.Length);
        var streaming = new PcmResampler();
        using var chunks = new MemoryStream();
        for (var offset = 0; offset < input.Length; offset += 514)
            chunks.Write(streaming.Convert(input.AsSpan(offset, Math.Min(514, input.Length - offset))));
        Assert.Equal(complete, chunks.ToArray());
        var low = Rms(complete.AsSpan(2000));
        var high = Rms(new PcmResampler().Convert(Tone(10000, 24000)).AsSpan(2000));
        Assert.InRange(low, 6500, 7500);
        Assert.True(high < low / 50, $"Alias energy too high: {high} / {low}");
        streaming.Reset();
        Assert.Equal(complete, streaming.Convert(input));
    }

    [Fact]
    public async Task InterruptionInvalidatesAlreadyDequeuedAudioAndClearsPendingAudio()
    {
        await using var f = await LiveFixture.Create();
        var socket = new FakeSocket(); f.Factory.Sockets.Enqueue(socket);
        var live = await f.Start();
        await f.Live.NegotiateAsync(live.AgentSessionId, new(1, new("pcm_16000", 16000, 1), new("pcm_16000", 16000, 1)), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = f.Live.ReadEventsAsync(live.AgentSessionId, timeout.Token).GetAsyncEnumerator();
        socket.Push(Audio(new byte[960]));
        Assert.True(await reader.MoveNextAsync()); var playing = reader.Current;
        socket.Push(new { serverContent = new { interrupted = true } });
        await Until(() => Task.FromResult(playing.PlaybackCancellation.IsCancellationRequested));
        Assert.True(await reader.MoveNextAsync()); Assert.Equal("Interruption", reader.Current.Type);
        socket.Push(Audio(new byte[960]));
        Assert.True(await reader.MoveNextAsync()); Assert.False(reader.Current.PlaybackCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task TranscriptsPersistBothDirectionsWithoutInventingNames()
    {
        await using var f = await LiveFixture.Create();
        var socket = new FakeSocket(); f.Factory.Sockets.Enqueue(socket); await f.Start();
        socket.Push(new { serverContent = new { inputTranscription = new { text = "Please summarize." }, outputTranscription = new { text = "I will prepare it." } } });
        await Until(async () => (await f.Fixture.Transcripts.ListAsync(x => true, 0, 100, default)).Count == 2);
        var saved = await f.Fixture.Transcripts.ListAsync(x => true, 0, 100, default);
        Assert.Equal(new[] { "user", "agent" }, saved.Select(x => x.SpeakerIdentifier));
        Assert.All(saved, x => { Assert.Equal(f.Session.Id, x.SessionId); Assert.Equal("gemini", x.Source); Assert.True(x.StartTime >= 0); });
        Assert.Equal(2, saved.Select(x => x.SourceEventId).Distinct().Count());
    }

    [Theory]
    [InlineData("shell", "{}", "unknown_tool")]
    [InlineData("create_quacktion", "{\"actionType\":\"email.send\",\"query\":\"hello\"}", "unsupported_tool_capability")]
    [InlineData("create_quacktion", "{\"actionType\":\"internal.summary\",\"query\":7}", "invalid_tool_arguments")]
    [InlineData("create_quacktion", "{\"actionType\":\"internal.summary\",\"query\":\"hello\",\"owner\":\"other\"}", "invalid_tool_arguments")]
    public async Task ToolArgumentsAreAllowlisted(string name, string args, string code)
    {
        await using var f = await LiveFixture.Create();
        var service = f.Services.GetRequiredService<MeetingToolService>();
        var call = await service.AcceptAsync(f.Account.Id, f.Session.Id, "call", name, JsonSerializer.Deserialize<JsonElement>(args), default);
        call = await service.ResolveAsync(call, default);
        Assert.Contains(code, call.ResultJson);
        Assert.Empty(await f.Fixture.Actions.ListAsync(x => true, 0, 100, default));
    }

    [Fact]
    public async Task VoiceMcpRequestRequiresAuthenticatedConfirmationAndKeepsPrivateResultsSilent()
    {
        await using var f = await LiveFixture.Create();
        var service = f.Services.GetRequiredService<MeetingToolService>();
        var args = JsonSerializer.SerializeToElement(new { request = "Check my email", announcementPolicy = "PRIVATE_NOTIFICATION" });
        var call = await service.AcceptAsync(f.Account.Id, f.Session.Id, "mcp-voice", "execute_quacktion", args, default);
        call = await service.ResolveAsync(call, default);
        var task = (await f.Fixture.Actions.GetAsync(call.QuacktionId!, default))!;
        Assert.Equal("mcp.request", task.ActionType);
        Assert.Equal(QuacktionStatus.PendingApproval, task.Status);
        Assert.Equal("awaiting_authenticated_confirmation", call.ProgressStatus);
        Assert.False(await f.Fixture.Executor.ExecuteNextAsync(default));
        Assert.Equal(0, f.Fixture.Agent.Calls);
        Assert.Null(call.ResultJson);
        await Assert.ThrowsAsync<AppException>(() => service.AcceptAsync("other-owner", f.Session.Id, "mcp-other", "execute_quacktion", args, default));
        // Simulate the confirmed private outcome; it must never be copied into a voice response.
        task.Status = QuacktionStatus.Completed;
        task.ResultJson = "confidential email content";
        await f.Fixture.Actions.ReplaceAsync(task, default);
        call = await service.ResolveAsync(call, default);
        Assert.Equal("SILENT", call.ResponseScheduling);
        Assert.DoesNotContain("confidential", call.ResultJson!);
    }

    [Fact]
    public async Task AsyncTaskIsIdempotentAndResultAnnouncementRequiresApproval()
    {
        await using var f = await LiveFixture.Create();
        var service = f.Services.GetRequiredService<MeetingToolService>();
        var args = JsonSerializer.SerializeToElement(new { actionType = "internal.summary", query = "Summarize the meeting" });
        var call = await service.AcceptAsync(f.Account.Id, f.Session.Id, "create-1", "create_quacktion", args, default);
        call = await service.ResolveAsync(call, default);
        Assert.Null(call.ResultJson); Assert.NotNull(call.QuacktionId);
        var replay = await service.AcceptAsync(f.Account.Id, f.Session.Id, "create-1", "create_quacktion", args, default);
        await service.ResolveAsync(replay, default);
        Assert.Single(await f.Fixture.Actions.ListAsync(x => true, 0, 100, default));
        f.Fixture.Agent.Run = (_, _) => Task.FromResult(new AgentTaskResult(true, "confidential result", null));
        Assert.True(await f.Fixture.Executor.ExecuteNextAsync(default));
        call = await service.ResolveAsync(await f.Calls.GetAsync(call.Id, default) ?? call, default);
        Assert.DoesNotContain("confidential", call.ResultJson!);
        var announce = await service.AcceptAsync(f.Account.Id, f.Session.Id, "announce-1", "announce_quacktion_result", JsonSerializer.SerializeToElement(new { quacktionId = call.QuacktionId }), default);
        announce = await service.ResolveAsync(announce, default);
        var approvalTask = (await f.Fixture.Actions.GetAsync(announce.QuacktionId!, default))!;
        Assert.Equal(QuacktionStatus.PendingApproval, approvalTask.Status);
        Assert.Null(announce.ResultJson);
        Assert.False(await f.Fixture.Executor.ExecuteNextAsync(default));
        await f.Fixture.Approvals.ResolveAsync(f.Account.Id, approvalTask.Id, approvalTask.ActionDigest, true, default);
        Assert.True(await f.Fixture.Executor.ExecuteNextAsync(default));
        announce = await service.ResolveAsync(announce, default);
        Assert.Contains("confidential result", announce.ResultJson);
        Assert.Contains("approvedAnnouncement", announce.ResultJson);
    }

    [Fact]
    public async Task ToolResponsesCorrelateMultipleCallsWhileAudioContinues()
    {
        await using var f = await LiveFixture.Create();
        var socket = new FakeSocket(); f.Factory.Sockets.Enqueue(socket);
        var live = await f.Start();
        await f.Live.NegotiateAsync(live.AgentSessionId, new(1, new("pcm_16000", 16000, 1), new("pcm_16000", 16000, 1)), default);
        socket.Push(new { toolCall = new { functionCalls = new[] {
            new { id = "one", name = "create_quacktion", args = new { actionType = "internal.summary", query = "Summary" } },
            new { id = "two", name = "create_quacktion", args = new { actionType = "internal.analysis", query = "Analysis" } }
        } } });
        await Until(async () => (await f.Fixture.Actions.ListAsync(x => true, 0, 100, default)).Count == 2);
        Assert.DoesNotContain(socket.Sent, x => x.TryGetProperty("toolResponse", out var response) && !response.GetProperty("functionResponses")[0].GetProperty("willContinue").GetBoolean());
        await f.Live.SendAudioAsync(live.AgentSessionId, new byte[960], default);
        Assert.Contains(socket.Sent, x => x.TryGetProperty("realtimeInput", out _));
        await f.Fixture.Executor.ExecuteNextAsync(default); await f.Fixture.Executor.ExecuteNextAsync(default);
        await Until(() => Task.FromResult(socket.Sent.Count(x => x.TryGetProperty("toolResponse", out var response) && !response.GetProperty("functionResponses")[0].GetProperty("willContinue").GetBoolean()) == 2));
        var responses = socket.Sent.Where(x => x.TryGetProperty("toolResponse", out var response) && !response.GetProperty("functionResponses")[0].GetProperty("willContinue").GetBoolean()).Select(x => x.GetProperty("toolResponse").GetProperty("functionResponses")[0]).ToArray();
        Assert.Equal(new[] { "one", "two" }, responses.Select(x => x.GetProperty("id").GetString()).Order());
        Assert.All(responses, x => Assert.Equal("WHEN_IDLE", x.GetProperty("scheduling").GetString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GoAwayAndNetworkCloseResumeWithoutEndingMeeting(bool goAway)
    {
        await using var f = await LiveFixture.Create();
        var first = new FakeSocket(); var second = new FakeSocket();
        f.Factory.Sockets.Enqueue(first); f.Factory.Sockets.Enqueue(second);
        var live = await f.Start();
        first.Push(new { sessionResumptionUpdate = new { newHandle = "resume-test", resumable = true } });
        await Until(async () => (await f.States.GetAsync(f.Session.Id, default))?.ResumptionHandle == "resume-test");
        if (goAway) first.Push(new { goAway = new { timeLeft = "5s" } });
        else first.CloseFromServer(WebSocketCloseStatus.EndpointUnavailable, "restarting");
        await Until(() => Task.FromResult(second.Sent.Any()));
        Assert.Equal("resume-test", second.Sent.First().GetProperty("setup").GetProperty("sessionResumption").GetProperty("handle").GetString());
        Assert.Equal(SessionStatus.Active, (await f.Fixture.Sessions.GetAsync(f.Session.Id, default))!.Status);
        await f.Live.SendUserTextAsync(live.AgentSessionId, "Continue", default);
        Assert.Contains(second.Sent, x => x.TryGetProperty("realtimeInput", out _));
    }

    [Fact]
    public async Task InvalidResumptionFallsBackToPersistedContextAndCleanupCancelsReceive()
    {
        await using var f = await LiveFixture.Create();
        var first = new FakeSocket(); var rejected = new FakeSocket(false); var fresh = new FakeSocket();
        rejected.CloseFromServer(WebSocketCloseStatus.InvalidPayloadData, "Invalid session handle");
        f.Factory.Sockets.Enqueue(first); f.Factory.Sockets.Enqueue(rejected); f.Factory.Sockets.Enqueue(fresh);
        var live = await f.Start();
        first.Push(new { serverContent = new { inputTranscription = new { text = "The deadline is Friday." } }, sessionResumptionUpdate = new { newHandle = "expired", resumable = true } });
        await Until(async () => (await f.Fixture.Transcripts.ListAsync(x => true, 0, 100, default)).Count == 1);
        first.Push(new { goAway = new { timeLeft = "1s" } });
        await Until(() => Task.FromResult(fresh.Sent.Any(x => x.TryGetProperty("clientContent", out _))));
        Assert.Contains(fresh.Sent, x => x.GetRawText().Contains("Friday"));
        Assert.False(fresh.Sent.First().GetProperty("setup").GetProperty("sessionResumption").TryGetProperty("handle", out _));
        await f.Live.EndSessionAsync(live.AgentSessionId, default);
        Assert.True(first.Disposed && rejected.Disposed && fresh.Disposed);
    }

    [Fact]
    public async Task PendingTaskSurvivesFreshConnectionAndKeepsFunctionResponseId()
    {
        await using var f = await LiveFixture.Create();
        var first = new FakeSocket(); var second = new FakeSocket();
        f.Factory.Sockets.Enqueue(first); f.Factory.Sockets.Enqueue(second);
        await f.Start();
        first.Push(new { toolCall = new { functionCalls = new[] { new { id = "durable", name = "create_quacktion", args = new { actionType = "internal.analysis", query = "Analyze our next steps" } } } } });
        await Until(() => Task.FromResult(first.Sent.Any(x => x.TryGetProperty("toolResponse", out _))));
        var progress = first.Sent.Single(x => x.TryGetProperty("toolResponse", out _)).GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.True(progress.GetProperty("willContinue").GetBoolean());
        Assert.Equal("SILENT", progress.GetProperty("scheduling").GetString());
        first.Push(new { goAway = new { timeLeft = "1s" } });
        await Until(() => Task.FromResult(second.Sent.Any(x => x.TryGetProperty("clientContent", out _))));
        Assert.Contains(second.Sent, x => x.GetRawText().Contains("durable"));
        Assert.True(await f.Fixture.Executor.ExecuteNextAsync(default));
        await Until(() => Task.FromResult(second.Sent.Any(x => x.TryGetProperty("toolResponse", out var response) && !response.GetProperty("functionResponses")[0].GetProperty("willContinue").GetBoolean())));
        var completed = second.Sent.Single(x => x.TryGetProperty("toolResponse", out var response) && !response.GetProperty("functionResponses")[0].GetProperty("willContinue").GetBoolean());
        Assert.Equal("durable", completed.GetProperty("toolResponse").GetProperty("functionResponses")[0].GetProperty("id").GetString());
        Assert.Single(await f.Fixture.Actions.ListAsync(x => true, 0, 100, default));
    }

    [Fact]
    public async Task ModelCancellationSuppressesResponseButDoesNotLoseDurableTask()
    {
        await using var f = await LiveFixture.Create();
        var socket = new FakeSocket(); f.Factory.Sockets.Enqueue(socket); await f.Start();
        socket.Push(new { toolCall = new { functionCalls = new[] { new { id = "cancelled-call", name = "create_quacktion", args = new { actionType = "internal.summary", query = "Summarize" } } } } });
        await Until(() => Task.FromResult(socket.Sent.Any(x => x.TryGetProperty("toolResponse", out _))));
        socket.Push(new { toolCallCancellation = new { ids = new[] { "cancelled-call" } } });
        await Until(async () => (await f.Calls.ListAsync(x => x.Cancelled, 0, 100, default)).Count == 1);
        Assert.True(await f.Fixture.Executor.ExecuteNextAsync(default));
        var q = Assert.Single(await f.Fixture.Actions.ListAsync(x => true, 0, 100, default));
        Assert.Equal(QuacktionStatus.Completed, q.Status);
        Assert.DoesNotContain(socket.Sent, x => x.TryGetProperty("toolResponse", out var response) && !response.GetProperty("functionResponses")[0].GetProperty("willContinue").GetBoolean());
    }

    [Fact]
    public async Task SetupTimeoutAndCallerCancellationDisposeProviderSocket()
    {
        await using var f = await LiveFixture.Create(new GeminiSettings { ApiKey = "mock", ConnectionTimeoutSeconds = 1 });
        var timedOut = new FakeSocket(false); f.Factory.Sockets.Enqueue(timedOut);
        Assert.Equal("gemini_connection_timeout", (await Assert.ThrowsAsync<AppException>(() => f.Start())).Code);
        Assert.True(timedOut.Disposed);
        var cancelled = new FakeSocket(false); f.Factory.Sockets.Enqueue(cancelled);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Live.StartSessionAsync(new(f.Session.Id, f.Account.Id), cancel.Token));
        Assert.True(cancelled.Disposed);
    }

    [Fact]
    public async Task MissingKeyFailsBeforeOpeningSocket()
    {
        await using var f = await LiveFixture.Create(new GeminiSettings());
        var error = await Assert.ThrowsAsync<AppException>(() => f.Start());
        Assert.Equal("gemini_api_key_not_configured", error.Code);
        Assert.Equal(0, f.Factory.Connections);
    }

    [GeminiFact]
    [Trait("Category", "LiveGemini")]
    public async Task OptInRealGeminiSetupAndSpokenResponse()
    {
        var key = Environment.GetEnvironmentVariable("QUACKTION_TEST_GEMINI_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var socket = await new GeminiSocketFactory().ConnectAsync(key, timeout.Token);
        await GeminiTransport.SendAsync(socket, GeminiProtocol.Setup(new GeminiSettings { ApiKey = key }), timeout.Token);
        Assert.NotNull((await GeminiTransport.ReceiveAsync(socket, timeout.Token)).SetupComplete);
        await GeminiTransport.SendAsync(socket, new { realtimeInput = new { text = "Quacktion, please say hello." } }, timeout.Token);
        while (true)
        {
            var response = await GeminiTransport.ReceiveAsync(socket, timeout.Token);
            if (response.ServerContent?.ModelTurn?.Parts?.Any(x => x.InlineData is { Data.Length: > 0 }) == true) break;
            Assert.Null(response.Error);
        }
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    private static object Audio(byte[] pcm) => new { serverContent = new { modelTurn = new { parts = new[] { new { inlineData = new { mimeType = "audio/pcm;rate=24000", data = Convert.ToBase64String(pcm) } } } } } };
    internal static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
    private static byte[] Tone(int hz, int samples)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(10000 * Math.Sin(2 * Math.PI * hz * i / 24000)));
        return pcm;
    }
    private static double Rms(ReadOnlySpan<byte> pcm)
    {
        double total = 0;
        for (var i = 0; i < pcm.Length; i += 2) { var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]); total += (double)sample * sample; }
        return Math.Sqrt(total / (pcm.Length / 2));
    }
}

internal sealed class LiveFixture : IAsyncDisposable
{
    public Fixture Fixture { get; } = new();
    public FakeSocketFactory Factory { get; } = new();
    public StateRepo States { get; } = new();
    public CallRepo Calls { get; } = new();
    public ServiceProvider Services { get; private set; } = null!;
    public GeminiLiveService Live { get; private set; } = null!;
    public Account Account { get; private set; } = null!;
    public Session Session { get; private set; } = null!;
    public static async Task<LiveFixture> Create(GeminiSettings? settings = null, TimeSpan? callInsertDelay = null)
    {
        var f = new LiveFixture();
        (f.Account, f.Session) = await f.Fixture.Seed();
        var services = new ServiceCollection();
        services.AddSingleton<IAccountRepo>(f.Fixture.Accounts);
        services.AddSingleton<ISessionRepo>(f.Fixture.Sessions);
        services.AddSingleton<ITranscriptRepo>(f.Fixture.Transcripts);
        services.AddSingleton<IQuacktionRepo>(f.Fixture.Actions);
        services.AddSingleton<IRealtimeStateRepo>(f.States);
        services.AddSingleton<IMeetingToolCallRepo>(callInsertDelay is { } delay
            ? new SlowCallRepo(f.Calls, delay) : f.Calls);
        services.AddSingleton<IQuacktionService>(f.Fixture.ActionService);
        services.AddSingleton<ITranscriptService>(new TranscriptService(f.Fixture.Transcripts, f.Fixture.SessionService, f.Fixture.Sessions, f.Fixture.Events));
        services.AddTransient<MeetingToolService>();
        f.Services = services.BuildServiceProvider();
        f.Live = new GeminiLiveService(f.Factory, Options.Create(settings ?? new GeminiSettings { ApiKey = "test-key" }), f.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<GeminiLiveService>.Instance);
        return f;
    }
    public Task<AgentSessionResult> Start() => Live.StartSessionAsync(new(Session.Id, Account.Id), default);
    public async ValueTask DisposeAsync() { await Live.DisposeAsync(); await Services.DisposeAsync(); }
}
internal sealed class StateRepo : MemoryRepo<RealtimeState>, IRealtimeStateRepo;
internal sealed class CallRepo : MemoryRepo<MeetingToolCall>, IMeetingToolCallRepo;
internal sealed class SlowCallRepo(CallRepo inner, TimeSpan delay) : IMeetingToolCallRepo
{
    public Task<MeetingToolCall?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
    public Task<MeetingToolCall?> FindAsync(System.Linq.Expressions.Expression<Func<MeetingToolCall, bool>> filter, CancellationToken ct) => inner.FindAsync(filter, ct);
    public Task<IReadOnlyList<MeetingToolCall>> ListAsync(System.Linq.Expressions.Expression<Func<MeetingToolCall, bool>> filter, int offset, int limit, CancellationToken ct) => inner.ListAsync(filter, offset, limit, ct);
    public async Task InsertAsync(MeetingToolCall value, CancellationToken ct) { await Task.Delay(delay, ct); await inner.InsertAsync(value, ct); }
    public Task<bool> ReplaceAsync(MeetingToolCall value, CancellationToken ct) => inner.ReplaceAsync(value, ct);
    public Task<bool> DeleteAsync(MeetingToolCall value, CancellationToken ct) => inner.DeleteAsync(value, ct);
}
internal sealed class FakeSocketFactory : IGeminiSocketFactory
{
    public ConcurrentQueue<FakeSocket> Sockets { get; } = new();
    public int Connections;
    public Task<WebSocket> ConnectAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Connections);
        return Task.FromResult<WebSocket>(Sockets.TryDequeue(out var socket) ? socket : throw new WebSocketException());
    }
}
internal sealed class FakeSocket : WebSocket
{
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private WebSocketState state = WebSocketState.Open;
    private byte[]? remainder;
    private WebSocketCloseStatus? closeStatus;
    private string? closeDescription;
    public ConcurrentQueue<JsonElement> Sent { get; } = new();
    public bool Disposed { get; private set; }
    public FakeSocket(bool ack = true) { if (ack) Push(new { setupComplete = new { } }); }
    public void Push(object value) => incoming.Writer.TryWrite((JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text));
    public void CloseFromServer(WebSocketCloseStatus status, string description)
    { closeStatus = status; closeDescription = description; incoming.Writer.TryWrite(([], WebSocketMessageType.Close)); }
    public override WebSocketCloseStatus? CloseStatus => closeStatus;
    public override string? CloseStatusDescription => closeDescription;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
    public override void Dispose() { Disposed = true; Abort(); }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) { state = WebSocketState.Closed; return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseAsync(status, description, ct);
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        byte[] data;
        if (remainder is not null) { data = remainder; remainder = null; }
        else
        {
            var item = await incoming.Reader.ReadAsync(ct);
            if (item.Type == WebSocketMessageType.Close) { state = WebSocketState.CloseReceived; return new(0, item.Type, true, closeStatus, closeDescription); }
            data = item.Data;
        }
        var length = Math.Min(data.Length, buffer.Count);
        data.AsSpan(0, length).CopyTo(buffer.AsSpan());
        if (length < data.Length) remainder = data[length..];
        return new(length, WebSocketMessageType.Text, remainder is null);
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state != WebSocketState.Open) throw new WebSocketException();
        Sent.Enqueue(JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan()));
        return Task.CompletedTask;
    }
}

public sealed class GeminiFactAttribute : FactAttribute
{
    public GeminiFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUACKTION_TEST_GEMINI_KEY")))
            Skip = "Set QUACKTION_TEST_GEMINI_KEY to run a live Gemini setup and audio smoke test.";
    }
}
