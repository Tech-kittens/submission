using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Quacktion.Data.Audio;
using Xunit;

namespace Quacktion.Web.Tests;

public sealed class GeminiApiFactory : ApiFactory
{
    internal FakeSocketFactory Provider { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "mock-key" }));
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IGeminiSocketFactory>(Provider);
            s.AddSingleton<IRealtimeStateRepo, StateRepo>();
            s.AddSingleton<IMeetingToolCallRepo, CallRepo>();
            s.AddSingleton<IConversationalAgentGateway>(sp => sp.GetRequiredService<GeminiLiveService>());
        });
    }
}

public sealed class GeminiIntegrationTests
{
    [Fact]
    public async Task ExistingEsp32AudioProtocolStreamsThroughGeminiAndListsPersistedTranscript()
    {
        await using var f = new GeminiApiFactory();
        var provider = new FakeSocket(); f.Provider.Sockets.Enqueue(provider);
        var (client, account, _) = await f.Login("gemini-device");
        using var scope = f.Services.CreateScope();
        var credential = Secrets.New();
        var device = new Device { OwnerAccountId = account.Id, CredentialHash = Secrets.Hash(credential), DeviceIdentifier = "gemini-test-device" };
        await scope.ServiceProvider.GetRequiredService<IDeviceRepo>().InsertAsync(device, default);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/start") { Content = JsonContent.Create(new { name = "Hardware protocol", requestId = "device-start" }) };
        request.Headers.Authorization = new("Device", credential);
        var created = await client.SendAsync(request); created.EnsureSuccessStatusCode();
        var start = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = start.GetProperty("sessionId").GetString()!;
        var wsClient = f.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add("quacktion.audio.v1");
        wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + credential;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await wsClient.ConnectAsync(new Uri(start.GetProperty("webSocketUrl").GetString()!), timeout.Token);
        var buffer = new byte[65536];
        Assert.Contains("session.started", await Text(socket, buffer, timeout.Token));
        var format = new AudioFormat("pcm_16000", 16000, 1);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "audio.start", audio = new AudioNegotiation(1, format, format) }, GeminiProtocol.Json), WebSocketMessageType.Text, true, timeout.Token);
        Assert.Contains("audio.ready", await Text(socket, buffer, timeout.Token));
        var pcm = new byte[960];
        await socket.SendAsync(pcm, WebSocketMessageType.Binary, true, timeout.Token);
        await GeminiTests.Until(() => Task.FromResult(provider.Sent.Any(x => x.TryGetProperty("realtimeInput", out _))));
        provider.Push(new { serverContent = new { inputTranscription = new { text = "Quacktion, hello." }, outputTranscription = new { text = "Hello." }, modelTurn = new { parts = new[] { new { inlineData = new { mimeType = "audio/pcm;rate=24000", data = Convert.ToBase64String(pcm) } } } } } });
        var output = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Binary, output.MessageType);
        Assert.Equal(640, output.Count); // 480 input samples at 24 kHz -> 320 at 16 kHz.
        provider.Push(new { serverContent = new { interrupted = true } });
        Assert.Contains("audio.clear", await Text(socket, buffer, timeout.Token));
        await GeminiTests.Until(async () => (await scope.ServiceProvider.GetRequiredService<ITranscriptRepo>().ListAsync(x => x.SessionId == sessionId, 0, 100, timeout.Token)).Count == 2);
        (await client.GetAsync($"/api/sessions/{sessionId}/transcript", timeout.Token)).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/sessions", timeout.Token)).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/sessions/{sessionId}", timeout.Token)).EnsureSuccessStatusCode();
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"session.end\"}"), WebSocketMessageType.Text, true, timeout.Token);
        Assert.Contains("session.ended", await Text(socket, buffer, timeout.Token));
        Assert.Null((await scope.ServiceProvider.GetRequiredService<IDeviceRepo>().GetAsync(device.Id, timeout.Token))!.ActiveSessionId);
    }

    [Fact]
    public async Task SetupFailureReleasesDeviceReservation()
    {
        await using var f = new GeminiApiFactory();
        var bad = new FakeSocket(false); bad.CloseFromServer(WebSocketCloseStatus.InvalidPayloadData, "bad setup"); f.Provider.Sockets.Enqueue(bad);
        var (client, account, _) = await f.Login("gemini-failure");
        using var scope = f.Services.CreateScope();
        var credential = Secrets.New();
        var device = new Device { OwnerAccountId = account.Id, CredentialHash = Secrets.Hash(credential), DeviceIdentifier = "rejected-device" };
        await scope.ServiceProvider.GetRequiredService<IDeviceRepo>().InsertAsync(device, default);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/start") { Content = JsonContent.Create(new { name = "Rejected setup", requestId = "reject-start" }) };
        request.Headers.Authorization = new("Device", credential);
        var created = await client.SendAsync(request);
        created.EnsureSuccessStatusCode();
        var start = await created.Content.ReadFromJsonAsync<JsonElement>();
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.SubProtocols.Add("quacktion.audio.v1");
        wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + credential;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await wsClient.ConnectAsync(new Uri(start.GetProperty("webSocketUrl").GetString()!), timeout.Token);
        Assert.Contains("gemini_invalid_setup", await Text(socket, new byte[8192], timeout.Token));
        var session = await scope.ServiceProvider.GetRequiredService<ISessionRepo>().GetAsync(start.GetProperty("sessionId").GetString()!, timeout.Token);
        Assert.Equal(SessionStatus.Failed, session!.Status);
        Assert.Null((await scope.ServiceProvider.GetRequiredService<IDeviceRepo>().GetAsync(device.Id, timeout.Token))!.ActiveSessionId);
    }

    [Fact]
    public async Task FlashUsesConfiguredModelAndGroundingWithoutExposingKeyInUrl()
    {
        await using var f = await LiveFixture.Create();
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"Verified information."}]},"groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://example.test","title":"Source"}}]}}]}
            """);
        var gateway = new GeminiTaskGateway(new HttpFactory(handler), Options.Create(new GeminiSettings { ApiKey = "private-key" }), f.Fixture.Actions, f.Fixture.Sessions, f.Fixture.Transcripts);
        var result = await gateway.ExecuteAsync(new("internal.research", "{\"query\":\"Research this topic\"}", "task-key", f.Account.Id, f.Session.Id), default);
        Assert.True(result.Success); Assert.Contains("groundingChunks", result.ResultJson);
        Assert.EndsWith("/v1beta/models/gemini-4-argon:generateContent", handler.Uri);
        Assert.DoesNotContain("private-key", handler.Uri);
        Assert.Equal("private-key", handler.ApiKey);
        Assert.Contains("googleSearch", handler.Body);
    }

    [Fact]
    public async Task FlashCannotBypassAnnouncementApprovalOrFakeResearchEvidence()
    {
        await using var f = await LiveFixture.Create();
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"Unverified."}]},"finishReason":"STOP"}]}""");
        var gateway = new GeminiTaskGateway(new HttpFactory(handler), Options.Create(new GeminiSettings { ApiKey = "key" }), f.Fixture.Actions, f.Fixture.Sessions, f.Fixture.Transcripts);
        var unapproved = await gateway.ExecuteAsync(new("meeting.announce", "{\"content\":\"private\"}", "missing", f.Account.Id, f.Session.Id), default);
        Assert.Equal("approval_required", unapproved.FailureCode);
        var research = await gateway.ExecuteAsync(new("internal.research", "{\"query\":\"A question\"}", "research", f.Account.Id, f.Session.Id), default);
        Assert.Equal("gemini_research_not_grounded", research.FailureCode);
    }

    [Fact]
    public async Task ToolOwnershipAndDeniedApprovalStayEnforcedAfterWorkerExecution()
    {
        await using var f = await LiveFixture.Create();
        var service = f.Services.GetRequiredService<MeetingToolService>();
        await Assert.ThrowsAsync<AppException>(() => service.AcceptAsync("other", f.Session.Id, "id", "get_quacktion_status", JsonSerializer.SerializeToElement(new { quacktionId = "id" }), default));
        var q = await f.Fixture.ActionService.ProposeAsync(f.Account.Id, f.Session.Id, new("internal.summary", "{}"), default);
        f.Fixture.Agent.Run = (_, _) => Task.FromResult(new AgentTaskResult(true, "secret result", null));
        await f.Fixture.Executor.ExecuteNextAsync(default);
        var call = await service.AcceptAsync(f.Account.Id, f.Session.Id, "announce", "announce_quacktion_result", JsonSerializer.SerializeToElement(new { quacktionId = q.Id }), default);
        call = await service.ResolveAsync(call, default);
        var proposed = (await f.Fixture.Actions.GetAsync(call.QuacktionId!, default))!;
        await f.Fixture.Approvals.ResolveAsync(f.Account.Id, proposed.Id, proposed.ActionDigest, false, default);
        Assert.False(await f.Fixture.Executor.ExecuteNextAsync(default));
        call = await service.ResolveAsync(call, default);
        Assert.Contains("Rejected", call.ResultJson);
        Assert.DoesNotContain("secret result", call.ResultJson!);
    }

    private static async Task<string> Text(WebSocket socket, byte[] buffer, CancellationToken ct)
    { var result = await socket.ReceiveAsync(buffer, ct); return Encoding.UTF8.GetString(buffer, 0, result.Count); }

    [Fact]
    public async Task MeetingTranscriptionSendsSixteenKhzWavAndKeepsProviderThoughtsOutOfTranscript()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"candidates":[{"content":{"parts":[{"thought":true,"text":"Private reasoning"},{"text":"A spoken sentence."}]},"finishReason":"STOP"}]}""");
        var transcriber = new GeminiMeetingTranscriber(new HttpFactory(handler), Options.Create(new GeminiSettings { ApiKey = "private-key" }));
        var pcm = Enumerable.Range(0, 1280).Select(i => (byte)i).ToArray();
        Assert.Equal("A spoken sentence.", await transcriber.TranscribeAsync(pcm, default));
        using var payload = JsonDocument.Parse(handler.Body);
        var inline = payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("inlineData");
        Assert.Equal("audio/wav", inline.GetProperty("mimeType").GetString());
        var wav = Convert.FromBase64String(inline.GetProperty("data").GetString()!);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(pcm, wav[44..]);
        Assert.False(payload.RootElement.TryGetProperty("tools", out _));
        Assert.DoesNotContain("private-key", handler.Uri);
    }
    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") }; }
    private sealed class RecordingHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public string Uri = "", ApiKey = "", Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri!.AbsoluteUri; ApiKey = request.Headers.GetValues("x-goog-api-key").Single();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(status) { Content = new StringContent(response) };
        }
    }
}
