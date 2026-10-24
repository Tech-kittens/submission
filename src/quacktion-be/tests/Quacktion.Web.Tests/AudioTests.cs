using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Audio;
using Xunit;
namespace Quacktion.Web.Tests;

public sealed class EchoAdapter : IAudioProviderAdapter
{
    private readonly Channel<ProviderEvent> events = Channel.CreateBounded<ProviderEvent>(4);
    public Task NegotiateAsync(string id, AudioNegotiation negotiation, CancellationToken ct) => Task.CompletedTask;
    public async Task SendAudioAsync(string id, ReadOnlyMemory<byte> frame, CancellationToken ct) => await events.Writer.WriteAsync(new("Audio", frame.ToArray()), ct);
    public async IAsyncEnumerable<ProviderEvent> ReadEventsAsync(string id, [EnumeratorCancellation] CancellationToken ct)
    { await foreach (var item in events.Reader.ReadAllAsync(ct)) yield return item; }
}
public sealed class AudioTests
{
    [Fact]
    public async Task NegotiatedDeviceAudioFlowsBothWaysAndReconnectReplacesOldConnection()
    {
        await using var factory = new ApiFactory();
        await using var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IAudioProviderAdapter, EchoAdapter>();
            s.Configure<AudioSettings>(o => { o.AllowedInputFormats = [new("test-codec", 8000, 1)]; o.AllowedOutputFormats = [new("test-codec", 8000, 1)]; });
        }));
        var client = f.CreateClient();
        using var scope = f.Services.CreateScope(); var services = scope.ServiceProvider;
        var account = await services.GetRequiredService<IAccountService>().RegisterAsync(new("audio@example.test", "secure-password-123", "Audio"), default);
        var device = new Device { OwnerAccountId = account.Id, DeviceIdentifier = "audio-test", CredentialHash = Secrets.Hash("1234567890123456789012345678901234567890123456789012345678901234") };
        await services.GetRequiredService<IDeviceRepo>().InsertAsync(device, default);
        var session = await services.GetRequiredService<ISessionService>().CreateAsync(account.Id, new("Audio", device.Id), default);
        await services.GetRequiredService<ISessionService>().StartAsync(account.Id, session.Id, default);
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device 1234567890123456789012345678901234567890123456789012345678901234"; wsClient.SubProtocols.Add("quacktion.v1");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/devices/{device.Id}"), ct.Token);
        var buffer = new byte[65536]; await ws.ReceiveAsync(buffer, ct.Token);
        await Text(ws, "{\"version\":1,\"type\":\"StartSession\",\"sessionId\":\"" + session.Id + "\"}", ct.Token); await ws.ReceiveAsync(buffer, ct.Token);
        await Text(ws, "{\"version\":1,\"type\":\"AudioStart\",\"audio\":{\"protocolVersion\":1,\"input\":{\"codec\":\"test-codec\",\"sampleRate\":8000,\"channels\":1},\"output\":{\"codec\":\"test-codec\",\"sampleRate\":8000,\"channels\":1}}}", ct.Token);
        var start = await ws.ReceiveAsync(buffer, ct.Token); Assert.Contains("AudioStart", Encoding.UTF8.GetString(buffer, 0, start.Count));
        byte[] audio = [1, 2, 3, 4]; await ws.SendAsync(audio, WebSocketMessageType.Binary, true, ct.Token);
        var received = await ws.ReceiveAsync(buffer, ct.Token); Assert.Equal(WebSocketMessageType.Binary, received.MessageType); Assert.Equal(audio, buffer[..received.Count]);
        await Text(ws, "{\"version\":1,\"type\":\"AudioStop\"}", ct.Token);
        var stopped = await ws.ReceiveAsync(buffer, ct.Token); Assert.Contains("AudioStop", Encoding.UTF8.GetString(buffer, 0, stopped.Count));
        await Text(ws, "{\"version\":1,\"type\":\"AudioStart\",\"audio\":{\"protocolVersion\":1,\"input\":{\"codec\":\"test-codec\",\"sampleRate\":8000,\"channels\":1},\"output\":{\"codec\":\"test-codec\",\"sampleRate\":8000,\"channels\":1}}}", ct.Token);
        await ws.ReceiveAsync(buffer, ct.Token);
        byte[] resumed = [9, 8, 7]; await ws.SendAsync(resumed, WebSocketMessageType.Binary, true, ct.Token);
        var resumedFrame = await ws.ReceiveAsync(buffer, ct.Token); Assert.Equal(resumed, buffer[..resumedFrame.Count]);
        using var replacement = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/devices/{device.Id}"), ct.Token);
        var connected = await replacement.ReceiveAsync(buffer, ct.Token); Assert.Contains("DeviceConnected", Encoding.UTF8.GetString(buffer, 0, connected.Count));
        Assert.True(services.GetRequiredService<DeviceConnectionManager>().IsConnected(device.Id));
        // The previous transport must no longer accept frames after authoritative replacement.
        try { var closed = await ws.ReceiveAsync(buffer, ct.Token); Assert.Equal(WebSocketMessageType.Close, closed.MessageType); }
        catch (Exception e) when (e is WebSocketException or IOException) { }
    }
    [Fact]
    public async Task CoordinatorRejectsForeignSessionAndUnconfiguredFormat()
    {
        var sessions = new App.Tests.Sessions(); var devices = new App.Tests.Devices(); var adapter = new EchoAdapter();
        var format = new AudioFormat("test-codec", 8000, 1);
        var coordinator = new AudioStreamCoordinator(sessions, devices, adapter, Microsoft.Extensions.Options.Options.Create(new AudioSettings { AllowedInputFormats = [format], AllowedOutputFormats = [format] }));
        var device = new Device { OwnerAccountId = "owner", ActiveSessionId = "session" }; await devices.InsertAsync(device, default);
        var session = new Session { Id = "session", OwnerAccountId = "other", DeviceId = device.Id, AgentSessionId = "agent", Status = SessionStatus.Active }; await sessions.InsertAsync(session, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppException>(() => coordinator.AttachAsync(device, session.Id, new(1, format, format), default))).Status);
        var unsupported = new AudioFormat("unknown", 1234, 1);
        Assert.Equal("unsupported_audio_format", (await Assert.ThrowsAsync<AppException>(() => coordinator.AttachAsync(device, session.Id, new(1, unsupported, unsupported), default))).Code);
    }
    [Fact]
    public async Task DeviceFramesAreBoundedAndAudioBeforeAssociationIsRejected()
    {
        await using var f = new ApiFactory(); var (client, account, _) = await f.Login();
        using var scope = f.Services.CreateScope(); var device = new Device { OwnerAccountId = account.Id, CredentialHash = Secrets.Hash(new string('a', 64)) }; await scope.ServiceProvider.GetRequiredService<IDeviceRepo>().InsertAsync(device, default);
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.ConfigureRequest = r => r.Headers["Authorization"] = "Device " + new string('a', 64); wsClient.SubProtocols.Add("quacktion.v1");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var buffer = new byte[4096];
        using (var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/devices/{device.Id}"), ct.Token))
        {
            await ws.ReceiveAsync(buffer, ct.Token); await ws.SendAsync(new byte[1], WebSocketMessageType.Binary, true, ct.Token);
            var error = await ws.ReceiveAsync(buffer, ct.Token); Assert.Contains("audio_not_started", Encoding.UTF8.GetString(buffer, 0, error.Count));
        }
        using (var ws = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/devices/{device.Id}"), ct.Token))
        {
            await ws.ReceiveAsync(buffer, ct.Token); await ws.SendAsync(new byte[65537], WebSocketMessageType.Text, true, ct.Token);
            var error = await ws.ReceiveAsync(buffer, ct.Token); Assert.Contains("message_too_large", Encoding.UTF8.GetString(buffer, 0, error.Count));
        }
    }
    private static Task Text(WebSocket socket, string text, CancellationToken ct) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
}
