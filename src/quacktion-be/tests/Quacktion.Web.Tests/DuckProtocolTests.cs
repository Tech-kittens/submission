using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.App.Tests;
using Quacktion.Data.Agents;
using Quacktion.Data.Audio;
using Xunit;

namespace Quacktion.Web.Tests;

internal sealed class Meetings : MemoryRepo<Meeting>, IMeetingRepo;
internal sealed class MeetingEvents : MemoryRepo<MeetingEvent>, IMeetingEventRepo;
internal sealed class MeetingSegments : MemoryRepo<MeetingSegment>, IMeetingSegmentRepo
{
    public Task<IReadOnlyList<MeetingSegment>> OrderedAsync(string meetingId, int offset, int limit, CancellationToken ct)
    {
        lock (Gate) return Task.FromResult<IReadOnlyList<MeetingSegment>>(Values.Values.Where(s => s.MeetingId == meetingId)
            .OrderBy(s => s.BootId, StringComparer.Ordinal).ThenBy(s => s.StartUptimeMs).ThenBy(s => s.SegmentId, StringComparer.Ordinal)
            .Skip(offset).Take(limit).Select(Clone).ToList());
    }
}
internal sealed class TestTranscriber : IMeetingTranscriber
{
    public int Calls;
    public Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    { Interlocked.Increment(ref Calls); return Task.FromResult($"Speech from {pcm.Length / 1280} frames"); }
}
internal sealed class DuckFactory : ApiFactory
{
    public FakeSocketFactory Provider { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IMeetingRepo, Meetings>(); s.AddSingleton<IMeetingSegmentRepo, MeetingSegments>();
            s.AddSingleton<IMeetingEventRepo, MeetingEvents>(); s.AddSingleton<TestTranscriber>();
            s.AddSingleton<IMeetingTranscriber>(sp => sp.GetRequiredService<TestTranscriber>());
            s.AddSingleton<IGeminiSocketFactory>(Provider);
            s.AddSingleton<IRealtimeStateRepo, StateRepo>(); s.AddSingleton<IMeetingToolCallRepo, CallRepo>();
            s.AddSingleton<IConversationalAgentGateway>(sp => sp.GetRequiredService<GeminiLiveService>());
            s.Configure<GeminiSettings>(o => { o.ApiKey = "test-key"; o.ReconnectAttempts = 1; });
        });
    }
}

public sealed class DuckProtocolTests
{
    private const string Credential = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly object AudioStart = new { version = 1, type = "audio.start", audio = new
    { protocolVersion = 1, input = new { codec = "pcm_16000", sampleRate = 16000, channels = 1 }, output = new { codec = "pcm_16000", sampleRate = 16000, channels = 1 } } };

    [Fact]
    public async Task MeetingRoutesAcceptDeviceCredentialsAndBearerTokens()
    {
        await using var f = new DuckFactory();
        var (bearer, account, _) = await f.Login("meeting-dual-auth");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var socket = await Connect(f, device.Id, ct);
        using var hardware = f.CreateClient();
        hardware.DefaultRequestHeaders.Authorization = new("Device", Credential);

        var started = await hardware.PostAsJsonAsync("/api/meetings/start",
            new { name = "Both clients", recordingConsent = true }, ct);
        started.EnsureSuccessStatusCode();
        var meeting = (await started.Content.ReadFromJsonAsync<Meeting>(ct))!;
        Assert.Equal(device.Id, meeting.DeviceId);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync($"/api/meetings/{meeting.Id}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hardware.GetAsync($"/api/meetings/{meeting.Id}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hardware.GetAsync("/api/meetings", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hardware.GetAsync($"/api/meetings/{meeting.Id}/status", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hardware.GetAsync($"/api/meetings/{meeting.Id}/segments", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hardware.GetAsync($"/api/meetings/{meeting.Id}/events", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hardware.PostAsync($"/api/meetings/{meeting.Id}/end", null, ct)).StatusCode);

        var wrongPath = await hardware.PostAsJsonAsync($"/api/devices/{Guid.NewGuid():N}/meetings",
            new { name = "Wrong device", recordingConsent = true }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPath.StatusCode);
        var wrongBody = await hardware.PostAsJsonAsync("/api/meetings/start",
            new { deviceId = Guid.NewGuid().ToString("N"), name = "Wrong device", recordingConsent = true }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrongBody.StatusCode);
        using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/meetings", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/devices/{device.Id}/meetings",
            new { name = "Device ID alone", recordingConsent = true }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/devices/{device.Id}/mode",
            new { mode = "meeting" }, ct)).StatusCode);
    }

    [Fact]
    public async Task BearerOnlyMeetingRecordsPhoneAudioWithoutADevice()
    {
        await using var f = new DuckFactory();
        var (mobile, _, _) = await f.Login("mobile-meeting");
        var (other, _, _) = await f.Login("other-mobile-meeting");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var start = await mobile.PostAsJsonAsync("/api/meetings",
            new { name = "Phone recording", recordingConsent = true }, ct);
        start.EnsureSuccessStatusCode();
        var meeting = (await start.Content.ReadFromJsonAsync<Meeting>(ct))!;
        Assert.Equal("", meeting.DeviceId);
        var retryStart = await mobile.PostAsJsonAsync("/api/meetings/start",
            new { name = "Phone recording", recordingConsent = true }, ct);
        retryStart.EnsureSuccessStatusCode();
        Assert.Equal(meeting.Id, (await retryStart.Content.ReadFromJsonAsync<Meeting>(ct))!.Id);
        using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/meetings",
            new { name = "No token", recordingConsent = true, deviceId = meeting.DeviceId }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mobile.GetAsync("/api/meetings", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mobile.GetAsync($"/api/meetings/{meeting.Id}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/meetings/{meeting.Id}", ct)).StatusCode);
        var path = $"/api/meetings/{meeting.Id}/audio/segments?startMs=0";
        var pcm = new byte[1280];
        pcm[0] = 1;
        try
        {
            async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes)
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("application/octet-stream");
                return await client.PostAsync(path, content, ct);
            }

            Assert.Equal(HttpStatusCode.NotFound, (await Upload(other, pcm)).StatusCode);
            var uploaded = await Upload(mobile, pcm);
            uploaded.EnsureSuccessStatusCode();
            var segment = (await uploaded.Content.ReadFromJsonAsync<MeetingSegment>(ct))!;
            Assert.Equal("Speech from 1 frames", segment.Transcript);
            Assert.Equal(HttpStatusCode.OK, (await Upload(mobile, pcm)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Upload(mobile, new byte[1280])).StatusCode);
            using (var outOfOrder = new ByteArrayContent(pcm))
            {
                outOfOrder.Headers.ContentType = new("application/octet-stream");
                Assert.Equal(HttpStatusCode.BadRequest,
                    (await mobile.PostAsync($"/api/meetings/{meeting.Id}/audio/segments?startMs=80", outOfOrder, ct)).StatusCode);
            }
            using (var next = new ByteArrayContent(pcm))
            {
                next.Headers.ContentType = new("application/octet-stream");
                (await mobile.PostAsync($"/api/meetings/{meeting.Id}/audio/segments?startMs=40", next, ct)).EnsureSuccessStatusCode();
            }
            var transcript = await mobile.GetFromJsonAsync<JsonElement>($"/api/meetings/{meeting.Id}/transcript", ct);
            Assert.Equal(2, transcript.GetProperty("items").GetArrayLength());
            (await mobile.PostAsync($"/api/meetings/{meeting.Id}/end", null, ct)).EnsureSuccessStatusCode();
            var store = f.Services.GetRequiredService<IMeetingAudioStore>();
            var (wav, length) = await store.FinalizeAsync(meeting.Id,
                f.Services.GetRequiredService<IMeetingSegmentRepo>(), ct);
            Assert.Equal(2604, length);
            Assert.True(File.Exists(wav));
        }
        finally { await f.Services.GetRequiredService<IMeetingAudioStore>().DeleteAsync(meeting.Id, ct); }
    }

    [Fact]
    public async Task TwoGeminiConversationsReuseDeviceSocketWithSixteenKhzRepliesAndBurstInput()
    {
        await using var f = new DuckFactory();
        var (_, account, _) = await f.Login("duck-legacy");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        for (var turn = 0; turn < 2; turn++)
        {
            var provider = new FakeSocket(); f.Provider.Sockets.Enqueue(provider);
            await Send(ws, new { version = 1, type = "session.start", requestId = "wake-" + turn, payload = new { promptFormat = "audio" } }, ct);
            var started = await Control(ws, "session.started", ct);
            await Send(ws, AudioStart, ct);
            await Control(ws, "audio.ready", ct);
            for (var i = 0; i < 40; i++) await ws.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
            while (provider.Sent.Where(p => p.TryGetProperty("realtimeInput", out _)).Sum(p =>
                Convert.FromBase64String(p.GetProperty("realtimeInput").GetProperty("audio").GetProperty("data").GetString()!).Length) < 40 * 1280)
                await Task.Delay(10, ct);
            provider.Push(new { serverContent = new { modelTurn = new { parts = new[] { new { inlineData = new
                { mimeType = "audio/pcm;rate=24000", data = Convert.ToBase64String(new byte[2400]) } } } } } });
            var buffer = new byte[65536];
            var replyBytes = 0;
            while (replyBytes < 1600)
            {
                var reply = await ws.ReceiveAsync(buffer, ct);
                Assert.Equal(WebSocketMessageType.Binary, reply.MessageType);
                replyBytes += reply.Count;
            }
            Assert.Equal(1600, replyBytes);
            await Send(ws, new { version = 1, type = "session.end", requestId = "end-" + turn }, ct);
            var ended = await Control(ws, "session.ended", ct);
            Assert.Equal(started.GetProperty("payload").GetProperty("sessionId").GetString(), ended.GetProperty("payload").GetProperty("sessionId").GetString());
            await Send(ws, new { version = 1, type = "heartbeat" }, ct);
            await Control(ws, "heartbeat", ct);
        }
        Assert.Equal(2, f.Provider.Connections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpEndNotifiesAudioSocketBeforeCloseForBothCredentialTypes(bool deviceAuth)
    {
        await using var f = new DuckFactory();
        var (client, account, token) = await f.Login("duck-end");
        var device = await SeedDevice(f, account.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/start") { Content = JsonContent.Create(new { name = "Duck" }) };
        if (deviceAuth) request.Headers.Authorization = new("Device", Credential);
        request.Headers.Add("Idempotency-Key", "external-end");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        var start = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("wss://", start.GetProperty("webSocketUrl").GetString());
        Assert.False(start.TryGetProperty("connectionCredential", out _));
        var id = start.GetProperty("sessionId").GetString();
        f.Provider.Sockets.Enqueue(new());
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.SubProtocols.Add("quacktion.audio.v1");
        wsClient.ConfigureRequest = r => r.Headers["Authorization"] = deviceAuth ? "Device " + Credential : "Bearer " + token.AccessToken;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/sessions/{id}/audio"), ct);
        await Control(ws, "session.started", ct);
        await Send(ws, AudioStart, ct); await Control(ws, "audio.ready", ct);
        (await client.PostAsync($"/api/sessions/{id}/end", null, ct)).EnsureSuccessStatusCode();
        await Control(ws, "session.ended", ct);
        var buffer = new byte[4096];
        Assert.Equal(WebSocketMessageType.Close, (await ws.ReceiveAsync(buffer, ct)).MessageType);
    }

    [Fact]
    public async Task OneMeetingPcmFrameAlsoFeedsActiveLiveConversation()
    {
        await using var f = new DuckFactory();
        var (client, account, _) = await f.Login("duck-one-stream");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        var provider = new FakeSocket(); f.Provider.Sockets.Enqueue(provider);
        await Send(ws, new { version = 1, type = "session.start", requestId = "one-stream", payload = new { promptFormat = "audio" } }, ct);
        await Control(ws, "session.started", ct);
        await Send(ws, AudioStart, ct); await Control(ws, "audio.ready", ct);
        (await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings", new { name = "One stream", recordingConsent = true }, ct)).EnsureSuccessStatusCode();
        await Control(ws, "mode.set", ct);
        await NegotiateMeeting(ws, ct);
        await Segment(ws, "boot-1", 0, null, ct);
        await Send(ws, new { version = 1, type = "heartbeat" }, ct); await Control(ws, "heartbeat", ct);
        for (var i = 0; i < 100 && !provider.Sent.Any(p => p.TryGetProperty("realtimeInput", out _)); i++)
            await Task.Delay(10, ct);
        Assert.Contains(provider.Sent, p => p.TryGetProperty("realtimeInput", out _));
        Assert.Equal(1, f.Services.GetRequiredService<TestTranscriber>().Calls);
    }

    [Fact]
    public async Task MeetingReconnectStoresBacklogContinuationsInUptimeOrderAndRespectsMute()
    {
        await using var f = new DuckFactory();
        var (client, account, _) = await f.Login("duck-meeting");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        var start = await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings", new { name = "Team meeting", recordingConsent = true }, ct);
        start.EnsureSuccessStatusCode();
        var meeting = (await start.Content.ReadFromJsonAsync<Meeting>(ct))!;
        await Control(ws, "mode.set", ct);
        await NegotiateMeeting(ws, ct);
        await Segment(ws, "boot-9", 1000, null, ct);
        await Send(ws, new { version = 1, type = "wake", segmentId = "boot-9", outcome = "session" }, ct);
        await Send(ws, new { version = 1, type = "heartbeat" }, ct); await Control(ws, "heartbeat", ct);
        // Replacement is a real reconnect, preserving the meeting association.
        using var replacement = await Connect(f, device.Id, ct);
        await NegotiateMeeting(replacement, ct);
        await Send(replacement, new { version = 1, type = "backlog.dropped", reason = "overflow", frames = 3, bytes = 3840 }, ct);
        await Segment(replacement, "boot-1", 1040, "boot-9", ct);
        await Segment(replacement, "boot-1", 1040, "boot-9", ct); // replay must not duplicate transcription
        await Send(replacement, new { version = 1, type = "heartbeat" }, ct); await Control(replacement, "heartbeat", ct);
        var segments = await f.Services.GetRequiredService<IMeetingSegmentRepo>().OrderedAsync(meeting.Id, 0, 100, ct);
        Assert.Equal(new[] { "boot-9", "boot-1" }, segments.Select(s => s.SegmentId));
        Assert.All(segments, s => { Assert.Equal(1, s.Frames); Assert.Equal(1280, s.Bytes); Assert.Null(s.StartMs); Assert.Null(s.FailureCode); });
        Assert.Equal("boot-9", segments[1].ContinuesSegmentId);
        Assert.Equal(2, f.Services.GetRequiredService<TestTranscriber>().Calls);
        await Send(replacement, new { version = 1, type = "mode.changed", mode = "off" }, ct);
        await Send(replacement, new { version = 1, type = "backlog.dropped", reason = "muted", frames = 2, bytes = 2560 }, ct);
        await Send(replacement, new { version = 1, type = "heartbeat" }, ct); await Control(replacement, "heartbeat", ct);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings", new { name = "Remote unmute", recordingConsent = true }, ct)).StatusCode);
        var events = await f.Services.GetRequiredService<IMeetingEventRepo>().ListAsync(e => e.MeetingId == meeting.Id, 0, 100, ct);
        Assert.Equal(4, events.Count);
        await Send(replacement, new { version = 1, type = "notify.finished" }, ct);
        await Control(replacement, "error", ct);
        await Send(replacement, new { version = 1, type = "heartbeat" }, ct); await Control(replacement, "heartbeat", ct);
        var (foreign, _, _) = await f.Login("foreign-meeting");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/meetings/{meeting.Id}/segments", ct)).StatusCode);
        var correctionPath = $"/api/meetings/{meeting.Id}/segments/boot-9/speaker";
        var correction = await client.PatchAsJsonAsync(correctionPath, new { speakerId = "participant-a", speakerName = "Sarah" }, ct);
        correction.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PatchAsJsonAsync(correctionPath,
            new { speakerId = "participant-a", speakerName = "Other" }, ct)).StatusCode);
        var speakers = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meeting.Id}/speakers", ct);
        Assert.Equal("Sarah", speakers[0].GetProperty("speakerName").GetString());
    }

    [Fact]
    public async Task BadSegmentCountsReturnErrorWithoutClosingControlConnection()
    {
        await using var f = new DuckFactory(); var (client, account, _) = await f.Login("duck-counts");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        (await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings", new { name = "Counts", recordingConsent = true }, ct)).EnsureSuccessStatusCode();
        await Control(ws, "mode.set", ct); await NegotiateMeeting(ws, ct);
        await Send(ws, new { version = 1, type = "segment.start", segmentId = "boot-1", startUptimeMs = 0, clockSource = "none" }, ct);
        await ws.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        await Send(ws, new { version = 1, type = "segment.end", segmentId = "boot-1", frames = 2, bytes = 2560, reason = "silence" }, ct);
        Assert.Equal("segment_count_mismatch", (await Control(ws, "error", ct)).GetProperty("code").GetString());
        await Send(ws, new { version = 1, type = "session.start", requestId = "ambiguous", payload = new { promptFormat = "audio" } }, ct);
        Assert.Equal("use_session_audio_socket", (await Control(ws, "session.start_failed", ct)).GetProperty("payload").GetProperty("code").GetString());
        await Send(ws, new { version = 1, type = "segment.start", segmentId = "boot-2", startUptimeMs = 100, clockSource = "none" }, ct);
        for (var i = 0; i < 751; i++) await ws.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        Assert.Equal("invalid_segment_audio", (await Control(ws, "error", ct)).GetProperty("code").GetString());
        await Segment(ws, "boot-3", 40000, null, ct);
        await Send(ws, new { version = 1, type = "heartbeat" }, ct); await Control(ws, "heartbeat", ct);
        Assert.Equal(1, f.Services.GetRequiredService<TestTranscriber>().Calls);
    }

    [Fact]
    public async Task MeetingAudioContinuesWhileAssistantUsesSeparateSessionSocket()
    {
        await using var f = new DuckFactory(); var (client, account, _) = await f.Login("duck-coexist");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var control = await Connect(f, device.Id, ct);
        var meetingResponse = await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings", new { name = "Coexist", recordingConsent = true }, ct);
        meetingResponse.EnsureSuccessStatusCode(); var meeting = (await meetingResponse.Content.ReadFromJsonAsync<Meeting>(ct))!;
        await Control(control, "mode.set", ct); await NegotiateMeeting(control, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/start") { Content = JsonContent.Create(new { name = "Assistant" }) };
        request.Headers.Authorization = new("Device", Credential); request.Headers.Add("Idempotency-Key", "simultaneous");
        var started = await client.SendAsync(request, ct); started.EnsureSuccessStatusCode();
        var sessionId = (await started.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sessionId").GetString();
        var provider = new FakeSocket(); f.Provider.Sockets.Enqueue(provider);
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.SubProtocols.Add("quacktion.audio.v1");
        wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + Credential;
        using var voice = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/sessions/{sessionId}/audio"), ct);
        await Control(voice, "session.started", ct); await Send(voice, AudioStart, ct); await Control(voice, "audio.ready", ct);
        await voice.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        await Segment(control, "boot-1", 0, null, ct);
        await Send(control, new { version = 1, type = "heartbeat" }, ct); await Control(control, "heartbeat", ct);
        await Send(voice, new { version = 1, type = "session.end" }, ct); await Control(voice, "session.ended", ct);
        await Segment(control, "boot-2", 40, null, ct);
        await Send(control, new { version = 1, type = "heartbeat" }, ct); await Control(control, "heartbeat", ct);
        Assert.Equal(2, (await f.Services.GetRequiredService<IMeetingSegmentRepo>().OrderedAsync(meeting.Id, 0, 100, ct)).Count);
        Assert.Null((await f.Services.GetRequiredService<IMeetingRepo>().GetAsync(meeting.Id, ct))!.EndedAt);
        (await client.PostAsync($"/api/meetings/{meeting.Id}/end", null, ct)).EnsureSuccessStatusCode();
        Assert.Equal("assistant", (await Control(control, "mode.set", ct)).GetProperty("mode").GetString());
        (await client.PostAsync($"/api/meetings/{meeting.Id}/end", null, ct)).EnsureSuccessStatusCode();
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meeting.Id}/status", ct);
        Assert.Equal("Ending", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PhysicalAssistantModeChangeEndsTheActiveMeeting()
    {
        await using var f = new DuckFactory(); var (client, account, _) = await f.Login("duck-end-meeting-button");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        var response = await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings",
            new { name = "Physical end", recordingConsent = true }, ct);
        response.EnsureSuccessStatusCode();
        var meeting = (await response.Content.ReadFromJsonAsync<Meeting>(ct))!;

        await Control(ws, "mode.set", ct);
        await Send(ws, new { version = 1, type = "mode.changed", mode = "meeting" }, ct);
        await Send(ws, new { version = 1, type = "mode.changed", mode = "assistant" }, ct);
        Assert.Equal("assistant", (await Control(ws, "mode.set", ct)).GetProperty("mode").GetString());

        var ended = await f.Services.GetRequiredService<IMeetingRepo>().GetAsync(meeting.Id, ct);
        Assert.NotNull(ended!.EndedAt);
        Assert.Equal(MeetingStatus.Ending, ended.Status);
    }

    [Fact]
    public async Task MeetingReconnectReattachesTheActiveLiveConversation()
    {
        await using var f = new DuckFactory(); var (client, account, _) = await f.Login("duck-meeting-resume");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
        using var first = await Connect(f, device.Id, ct);
        var provider = new FakeSocket(); f.Provider.Sockets.Enqueue(provider);
        await Send(first, new { version = 1, type = "session.start", requestId = "meeting-resume", payload = new { promptFormat = "audio" } }, ct);
        var started = await Control(first, "session.started", ct);
        var sessionId = started.GetProperty("payload").GetProperty("sessionId").GetString()!;
        await Send(first, AudioStart, ct); await Control(first, "audio.ready", ct);

        var response = await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings",
            new { name = "Reconnect live conversation", recordingConsent = true }, ct);
        response.EnsureSuccessStatusCode();
        var meeting = (await response.Content.ReadFromJsonAsync<Meeting>(ct))!;
        await Control(first, "mode.set", ct);
        await Send(first, new { version = 1, type = "mode.changed", mode = "meeting" }, ct);
        await NegotiateMeeting(first, ct);

        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect", ct);
        for (var i = 0; i < 100 && (await f.Services.GetRequiredService<ISessionRepo>().GetAsync(sessionId, ct))?.Status != SessionStatus.Suspended; i++)
            await Task.Delay(10, ct);
        Assert.Equal(SessionStatus.Suspended, (await f.Services.GetRequiredService<ISessionRepo>().GetAsync(sessionId, ct))!.Status);
        var resumedProvider = new FakeSocket(); f.Provider.Sockets.Enqueue(resumedProvider);
        using var replacement = await Connect(f, device.Id, ct);
        var resumed = await Control(replacement, "session.started", ct);
        Assert.Equal("Active", resumed.GetProperty("payload").GetProperty("status").GetString());
        await NegotiateMeeting(replacement, ct);
        await Segment(replacement, "boot-resume", 0, null, ct);
        for (var i = 0; i < 100 && !resumedProvider.Sent.Any(p => p.TryGetProperty("realtimeInput", out _)); i++)
            await Task.Delay(10, ct);
        Assert.Contains(resumedProvider.Sent, p => p.TryGetProperty("realtimeInput", out _));
        Assert.NotNull(await f.Services.GetRequiredService<IMeetingRepo>().GetAsync(meeting.Id, ct));
    }

    [Fact]
    public async Task DisconnectSavesPartialSegmentAndReplayExtendsItWithoutLosingTheMeeting()
    {
        await using var f = new DuckFactory(); var (client, account, _) = await f.Login("duck-partial");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        var start = await client.PostAsJsonAsync($"/api/devices/{device.Id}/meetings", new { name = "Partial", recordingConsent = true }, ct);
        start.EnsureSuccessStatusCode(); var meeting = (await start.Content.ReadFromJsonAsync<Meeting>(ct))!;
        await Control(ws, "mode.set", ct); await NegotiateMeeting(ws, ct);
        var segmentStart = new { version = 1, type = "segment.start", segmentId = "boot-1", startUptimeMs = 0, clockSource = "none" };
        await Send(ws, segmentStart, ct);
        await ws.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        await Send(ws, new { version = 1, type = "heartbeat" }, ct); await Control(ws, "heartbeat", ct);
        using var replacement = await Connect(f, device.Id, ct);
        var repo = f.Services.GetRequiredService<IMeetingSegmentRepo>();
        MeetingSegment? partial;
        while ((partial = await repo.FindAsync(s => s.MeetingId == meeting.Id, ct))?.Transcript is null) await Task.Delay(10, ct);
        Assert.Equal("disconnect", partial.Reason); Assert.Equal(1280, partial.Bytes);
        await NegotiateMeeting(replacement, ct);
        await Send(replacement, segmentStart, ct);
        await replacement.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        await replacement.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        await Send(replacement, new { version = 1, type = "segment.end", segmentId = "boot-1", frames = 2, bytes = 2560, reason = "silence" }, ct);
        await Send(replacement, new { version = 1, type = "heartbeat" }, ct); await Control(replacement, "heartbeat", ct);
        var complete = Assert.Single(await repo.OrderedAsync(meeting.Id, 0, 100, ct));
        Assert.Equal("silence", complete.Reason); Assert.Equal(2, complete.Frames);
        Assert.Equal("Speech from 2 frames", complete.Transcript);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderTerminationNotifiesDeviceAndLeavesHeartbeatAvailable(bool failure)
    {
        await using var f = new DuckFactory(); var (_, account, _) = await f.Login("duck-failure");
        var device = await SeedDevice(f, account.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        using var ws = await Connect(f, device.Id, ct);
        var provider = new FakeSocket(); f.Provider.Sockets.Enqueue(provider);
        await Send(ws, new { version = 1, type = "session.start", requestId = "failure", payload = new { promptFormat = "audio" } }, ct);
        await Control(ws, "session.started", ct); await Send(ws, AudioStart, ct); await Control(ws, "audio.ready", ct);
        provider.CloseFromServer(failure ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure, "test disconnect");
        var buffer = new byte[65536]; JsonElement control;
        do
        {
            var received = await ws.ReceiveAsync(buffer, ct);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            control = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, received.Count));
        } while (control.GetProperty("type").GetString() == "audio.clear");
        Assert.Equal("session.suspended", control.GetProperty("type").GetString());
        Assert.Equal("Suspended", control.GetProperty("payload").GetProperty("status").GetString());
        await Send(ws, new { version = 1, type = "heartbeat" }, ct); await Control(ws, "heartbeat", ct);
        Assert.NotNull((await f.Services.GetRequiredService<IDeviceRepo>().GetAsync(device.Id, ct))!.ActiveSessionId);
        Assert.Equal(SessionStatus.Suspended, (await f.Services.GetRequiredService<ISessionRepo>().GetAsync(
            (await f.Services.GetRequiredService<IDeviceRepo>().GetAsync(device.Id, ct))!.ActiveSessionId!, ct))!.Status);
    }

    private static async Task<Device> SeedDevice(ApiFactory f, string owner)
    {
        var device = new Device { OwnerAccountId = owner, DeviceIdentifier = "duck", CredentialHash = Secrets.Hash(Credential) };
        await f.Services.GetRequiredService<IDeviceRepo>().InsertAsync(device, default);
        return device;
    }
    private static async Task<WebSocket> Connect(ApiFactory f, string deviceId, CancellationToken ct)
    {
        var client = f.Server.CreateWebSocketClient(); client.SubProtocols.Add("quacktion.v1");
        client.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + Credential;
        var ws = await client.ConnectAsync(new Uri($"ws://localhost/ws/devices/{deviceId}"), ct);
        await Control(ws, "DeviceConnected", ct); return ws;
    }
    private static async Task NegotiateMeeting(WebSocket ws, CancellationToken ct)
    {
        await Send(ws, new { version = 1, type = "mode.changed", mode = "meeting" }, ct);
        await Send(ws, new { version = 1, type = "transcribe.start", format = "pcm_16000", frameMs = 40, preRollMs = 300, hangoverMs = 500 }, ct);
        await Control(ws, "transcribe.ready", ct);
    }
    private static async Task Segment(WebSocket ws, string id, long uptime, string? continues, CancellationToken ct)
    {
        await Send(ws, new { version = 1, type = "segment.start", segmentId = id, startMs = 123, startUptimeMs = uptime,
            clockSource = "none", preRollMs = 300, backlog = true, continuesSegmentId = continues }, ct);
        await ws.SendAsync(new byte[1280], WebSocketMessageType.Binary, true, ct);
        await Send(ws, new { version = 1, type = "segment.end", segmentId = id, frames = 1, bytes = 1280, reason = "silence" }, ct);
    }
    private static Task Send(WebSocket ws, object value, CancellationToken ct) => ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);
    private static async Task<JsonElement> Control(WebSocket ws, string expected, CancellationToken ct)
    {
        var buffer = new byte[65536]; var message = await ws.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        var json = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, message.Count));
        Assert.Equal(expected, (json.TryGetProperty("type", out var type) ? type : json.GetProperty("Type")).GetString()); return json;
    }
}
