using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Tests;
using Quacktion.Data.Audio;
using Quacktion.Web.Infrastructure;
using Xunit;
namespace Quacktion.Web.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = "test-only-key-which-is-at-least-32-bytes-long" }));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IHostedService>();
            s.AddSingleton(new DatabaseReadiness { Ready = true });
            s.AddSingleton<IAccountRepo, Accounts>(); s.AddSingleton<IRefreshTokenRepo, Tokens>();
            s.AddSingleton<IDeviceRepo, Devices>(); s.AddSingleton<IPairingChallengeRepo, Challenges>(); s.AddSingleton<ISessionRepo, Sessions>();
            s.AddSingleton<ITranscriptRepo, Transcripts>(); s.AddSingleton<IQuacktionRepo, Actions>(); s.AddSingleton<IAgentRepo, Agents>();
            s.AddSingleton<Voice>(); s.AddSingleton<IConversationalAgentGateway>(sp => sp.GetRequiredService<Voice>());
        });
    }
    public async Task<(HttpClient Client, Account Account, TokenResult Token)> Login(string prefix = "user")
    {
        var client = CreateClient();
        var registered = await client.PostAsJsonAsync("/api/accounts", new RegisterRequest(prefix + "@example.test", "secure-password-123", "Test")); registered.EnsureSuccessStatusCode();
        var account = (await registered.Content.ReadFromJsonAsync<Account>())!;
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(prefix + "@example.test", "secure-password-123")); response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResult>())!; client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        return (client, account, token);
    }
}
public sealed class SessionAudioApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IAudioProviderAdapter, EchoAdapter>();
            s.Configure<AudioSettings>(o => { o.AllowedInputFormats = [new("test-pcm", 16000, 1)]; o.AllowedOutputFormats = [new("test-pcm", 16000, 1)]; });
        });
    }
}
public sealed class ApiTests
{
    [Fact]
    public async Task BearerAuthenticatedSessionConnectsAndProxiesAudioThenEndsCleanly()
    {
        await using var f = new SessionAudioApiFactory();
        var (client, account, token) = await f.Login("session-audio");
        var created = await client.PostAsJsonAsync("/api/devices", new DeviceRequest("esp-session-audio", "ESP"));
        var device = await created.Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = device.GetProperty("device").GetProperty("id").GetString()!;
        var pairing = device.GetProperty("pairing");
        using var paired = await client.PostAsJsonAsync($"/api/devices/{deviceId}/pair", new PairRequest(pairing.GetProperty("challengeId").GetString()!, pairing.GetProperty("challenge").GetString()!));
        paired.EnsureSuccessStatusCode();
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/start")
        {
            Content = JsonContent.Create(new { name = "Audio test" })
        };
        startRequest.Headers.Add("Idempotency-Key", "audio-start-001");
        var startResponse = await client.SendAsync(startRequest);
        startResponse.EnsureSuccessStatusCode();
        var started = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = started.GetProperty("sessionId").GetString()!;
        Assert.Equal("Ready", started.GetProperty("status").GetString());
        Assert.Contains($"/ws/sessions/{sessionId}/audio", started.GetProperty("webSocketUrl").GetString());
        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/start")
        {
            Content = JsonContent.Create(new { name = "Audio test" })
        };
        retryRequest.Headers.Add("Idempotency-Key", "audio-start-001");
        var retryResponse = await client.SendAsync(retryRequest);
        retryResponse.EnsureSuccessStatusCode();
        var retried = await retryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sessionId, retried.GetProperty("sessionId").GetString());

        var wsClient = f.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Bearer " + token.AccessToken;
        wsClient.SubProtocols.Add("quacktion.audio.v1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/sessions/{sessionId}/audio"), timeout.Token);
        var buffer = new byte[65536];
        var active = await ws.ReceiveAsync(buffer, timeout.Token);
        var startedControl = JsonDocument.Parse(buffer.AsMemory(0, active.Count)).RootElement;
        Assert.Equal("session.started", startedControl.GetProperty("type").GetString());
        Assert.Equal("audio-start-001", startedControl.GetProperty("requestId").GetString());
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"audio.start\",\"audio\":{\"protocolVersion\":1,\"input\":{\"codec\":\"test-pcm\",\"sampleRate\":16000,\"channels\":1},\"output\":{\"codec\":\"test-pcm\",\"sampleRate\":16000,\"channels\":1}}}"), WebSocketMessageType.Text, true, timeout.Token);
        var ready = await ws.ReceiveAsync(buffer, timeout.Token);
        Assert.Contains("audio.ready", Encoding.UTF8.GetString(buffer, 0, ready.Count));
        using (var replay = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/sessions/{sessionId}/audio"), timeout.Token))
        {
            var rejected = await replay.ReceiveAsync(buffer, timeout.Token);
            var rejectedControl = JsonDocument.Parse(buffer.AsMemory(0, rejected.Count)).RootElement;
            Assert.Equal("session.start_failed", rejectedControl.GetProperty("type").GetString());
            Assert.Equal("audio-start-001", rejectedControl.GetProperty("requestId").GetString());
        }
        byte[] pcm = [1, 2, 3, 4, 5, 6];
        await ws.SendAsync(pcm, WebSocketMessageType.Binary, true, timeout.Token);
        var received = await ws.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
        Assert.Equal(pcm, buffer[..received.Count]);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"session.end\",\"requestId\":\"end-01\"}"), WebSocketMessageType.Text, true, timeout.Token);
        var ended = await ws.ReceiveAsync(buffer, timeout.Token);
        var endedJson = JsonDocument.Parse(buffer.AsMemory(0, ended.Count)).RootElement;
        Assert.Equal("session.ended", endedJson.GetProperty("type").GetString());
        using var scope = f.Services.CreateScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<ISessionRepo>().GetAsync(sessionId, timeout.Token);
        Assert.Equal(account.Id, persisted!.OwnerAccountId);
        Assert.Equal(deviceId, persisted.DeviceId);
        Assert.Equal(SessionStatus.Completed, persisted.Status);
    }

    [Fact]
    public async Task ControllersDiSwaggerAndAuthenticationAreFunctional()
    {
        await using var f = new ApiFactory(); var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/accounts/me")).StatusCode);
        var (client, _, _) = await f.Login(); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/accounts/me")).StatusCode);
        var swagger = await client.GetStringAsync("/swagger/v1/swagger.json");
        foreach (var route in new[] { "/api/accounts/me", "/api/auth/refresh", "/api/devices/{id}/pair", "/api/sessions/{id}/start", "/api/quacktions/{id}/approve", "/api/agents" }) Assert.Contains(route, swagger);
        using var scope = f.Services.CreateScope();
        foreach (var type in new[] { typeof(IAccountService), typeof(IDeviceService), typeof(ISessionService), typeof(IQuacktionExecutionService), typeof(IApprovalService), typeof(IAudioStreamCoordinator), typeof(IAgentOrchestrator), typeof(ITranscriptService), typeof(IResearchService), typeof(IClaimVerificationService) }) Assert.NotNull(scope.ServiceProvider.GetRequiredService(type));
    }
    [Fact]
    public async Task OwnershipErrorsAndValidationUseProblemDetails()
    {
        await using var f = new ApiFactory(); var (first, _, _) = await f.Login("first"); var (second, _, _) = await f.Login("second");
        var created = await first.PostAsJsonAsync("/api/sessions", new SessionRequest("Meeting", null)); created.EnsureSuccessStatusCode();
        var json = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = json.GetProperty("id").GetString();
        var denied = await second.GetAsync("/api/sessions/" + id); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); Assert.Equal("application/problem+json", denied.Content.Headers.ContentType!.MediaType);
        var bad = await first.PostAsJsonAsync("/api/sessions", new { title = "" }); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.DeleteAsync("/api/accounts/arbitrary-id")).StatusCode);
    }
    [Fact]
    public async Task DeletedAccountJwtIsImmediatelyRejected()
    {
        await using var f = new ApiFactory(); var (client, _, _) = await f.Login();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/accounts/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts/me")).StatusCode);
    }
    [Fact]
    public async Task HttpRefreshRotationAndApprovalAreNotCompletion()
    {
        await using var f = new ApiFactory(); var (client, _, token) = await f.Login();
        var rotated = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken)); rotated.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
        var created = await client.PostAsJsonAsync("/api/sessions", new SessionRequest("Meeting", null));
        var session = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        (await client.PostAsync($"/api/sessions/{session}/start", null)).EnsureSuccessStatusCode();
        var proposed = await client.PostAsJsonAsync($"/api/sessions/{session}/quacktions", new ProposalRequest("email.send", "{}")); proposed.EnsureSuccessStatusCode();
        var action = await proposed.Content.ReadFromJsonAsync<JsonElement>();
        var approved = await client.PostAsJsonAsync($"/api/quacktions/{action.GetProperty("id").GetString()}/approve", new ApprovalDecision(action.GetProperty("actionDigest").GetString()!));
        Assert.Equal(HttpStatusCode.Accepted, approved.StatusCode); Assert.Equal("Queued", (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
    [Fact]
    public async Task DeviceSocketAuthenticatesBeforeUpgradeAndSupportsHeartbeat()
    {
        await using var f = new ApiFactory(); var (client, _, _) = await f.Login();
        var created = await client.PostAsJsonAsync("/api/devices", new DeviceRequest("esp-test", "ESP")); var device = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = device.GetProperty("device").GetProperty("id").GetString(); var challenge = device.GetProperty("pairing");
        var paired = await client.PostAsJsonAsync($"/api/devices/{id}/pair", new PairRequest(challenge.GetProperty("challengeId").GetString()!, challenge.GetProperty("challenge").GetString()!));
        var credential = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credential").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/ws/devices/{id}")).StatusCode);
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + credential; wsClient.SubProtocols.Add("quacktion.v1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/devices/{id}"), timeout.Token);
        var buffer = new byte[4096]; var first = await ws.ReceiveAsync(buffer, timeout.Token); Assert.Contains("DeviceConnected", Encoding.UTF8.GetString(buffer, 0, first.Count));
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"Heartbeat\"}"), WebSocketMessageType.Text, true, timeout.Token);
        var heartbeat = await ws.ReceiveAsync(buffer, timeout.Token); Assert.Contains("Heartbeat", Encoding.UTF8.GetString(buffer, 0, heartbeat.Count));
        await client.PostAsync($"/api/devices/{id}/unpair", null);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, $"/ws/devices/{id}"); invalid.Headers.Authorization = new AuthenticationHeaderValue("Device", credential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(invalid)).StatusCode);
    }
    [Fact]
    public async Task AuthenticatedWakeWordMessageCreatesOneSessionAndSendsInitialPromptOnce()
    {
        await using var f = new ApiFactory(); var (client, account, _) = await f.Login("wake");
        var created = await client.PostAsJsonAsync("/api/devices", new DeviceRequest("esp-wake", "ESP"));
        var device = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = device.GetProperty("device").GetProperty("id").GetString()!;
        var pairing = device.GetProperty("pairing");
        var paired = await client.PostAsJsonAsync($"/api/devices/{id}/pair", new PairRequest(pairing.GetProperty("challengeId").GetString()!, pairing.GetProperty("challenge").GetString()!));
        var credential = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credential").GetString();
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + credential; wsClient.SubProtocols.Add("quacktion.v1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/devices/{id}"), timeout.Token);
        var buffer = new byte[4096]; await ws.ReceiveAsync(buffer, timeout.Token);
        const string command = "{\"version\":1,\"type\":\"session.start\",\"requestId\":\"wake-request-01\",\"payload\":{\"initialPrompt\":\"What is the current status of our sprint?\",\"promptFormat\":\"text\"}}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, timeout.Token);
        var first = await ws.ReceiveAsync(buffer, timeout.Token); var response = JsonDocument.Parse(buffer.AsMemory(0, first.Count)).RootElement.Clone();
        Assert.Equal("session.started", response.GetProperty("type").GetString());
        var sessionId = response.GetProperty("payload").GetProperty("sessionId").GetString();
        await ws.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, timeout.Token);
        var retry = await ws.ReceiveAsync(buffer, timeout.Token); var retryResponse = JsonDocument.Parse(buffer.AsMemory(0, retry.Count)).RootElement.Clone();
        Assert.Equal(sessionId, retryResponse.GetProperty("payload").GetProperty("sessionId").GetString());
        using var scope = f.Services.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<ISessionRepo>().GetAsync(sessionId!, timeout.Token);
        Assert.Equal(account.Id, session!.OwnerAccountId); Assert.Equal(id, session.DeviceId); Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(new[] { "What is the current status of our sprint?" }, f.Services.GetRequiredService<Voice>().UserTexts);
    }
    [Fact]
    public async Task SignalRRejectsForeignSessionSubscription()
    {
        await using var f = new ApiFactory(); var (client, _, token) = await f.Login();
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Bearer " + token.AccessToken;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/hubs/sessions"), timeout.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096]; await ws.ReceiveAsync(buffer, timeout.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":1,\"invocationId\":\"1\",\"target\":\"JoinSession\",\"arguments\":[\"foreign-session\"]}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var result = await ws.ReceiveAsync(buffer, timeout.Token); Assert.Contains("not_found", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
    [Fact]
    public async Task DatabaseUnavailableReturns503WithoutLeakingConfiguration()
    {
        await using var f = new ApiFactory(); var client = f.CreateClient(); f.Services.GetRequiredService<DatabaseReadiness>().Ready = false;
        var result = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("a@example.test", "password"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode); Assert.DoesNotContain("mongodb://", await result.Content.ReadAsStringAsync());
    }
}
