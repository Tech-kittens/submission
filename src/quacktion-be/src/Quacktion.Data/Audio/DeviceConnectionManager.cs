using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Quacktion.App.Interfaces;
namespace Quacktion.Data.Audio;

public sealed record OutboundFrame(byte[] Data, WebSocketMessageType Type, TaskCompletionSource? Sent = null,
    CancellationToken PlaybackCancellation = default, string? TurnId = null, long ReceivedAt = 0, IReadOnlyList<OutboundFrame>? Batch = null);
public sealed class DeviceConnection(WebSocket socket, CancellationToken hostToken) : IDisposable
{
    public string? PendingBriefingNotificationId { get; set; }
    public bool AudioActive { get; set; }
    public DateTime LastAssistantAudioAt { get; set; }
    public string? SessionId { get; set; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public WebSocket Socket { get; } = socket;
    public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
    public Channel<OutboundFrame> Outgoing { get; } = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    public void Stop() { Lifetime.Cancel(); Socket.Abort(); Outgoing.Writer.TryComplete(); }
    public void Dispose() { Lifetime.Dispose(); Socket.Dispose(); }
}
/// <summary>One transport host per deployment. Reconnect atomically replaces and aborts the old connection.</summary>
public sealed class DeviceConnectionManager : IDeviceConnectionManager
{
    private readonly ConcurrentDictionary<string, DeviceConnection> connections = new();
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    public DeviceConnection Register(string device, WebSocket socket, CancellationToken ct)
    {
        var next = new DeviceConnection(socket, ct);
        lock (gate) { if (connections.TryGetValue(device, out var old)) old.Stop(); connections[device] = next; }
        return next;
    }
    public DeviceConnection? Get(string deviceId) => connections.GetValueOrDefault(deviceId);
    public string[] ConnectedDeviceIds => connections.Keys.ToArray();
    public bool IsConnected(string deviceId) => connections.ContainsKey(deviceId);
    public bool IsCurrent(string deviceId, DeviceConnection connection) => connections.TryGetValue(deviceId, out var current) && ReferenceEquals(current, connection);
    public void Remove(string deviceId, DeviceConnection connection)
    { lock (gate) { if (IsCurrent(deviceId, connection)) connections.TryRemove(deviceId, out _); } }
    public Task DisconnectAsync(string deviceId, CancellationToken ct)
    { lock (gate) { if (connections.TryRemove(deviceId, out var connection)) connection.Stop(); } return Task.CompletedTask; }
    public async Task SendBinaryAsync(string deviceId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!connections.TryGetValue(deviceId, out var connection) || connection.Lifetime.IsCancellationRequested)
            return;
        await connection.Outgoing.Writer.WriteAsync(new(data.ToArray(), WebSocketMessageType.Binary), ct);
    }
    public async Task SendTextAsync(string deviceId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!connections.TryGetValue(deviceId, out var connection) || connection.Lifetime.IsCancellationRequested)
            return;
        await connection.Outgoing.Writer.WriteAsync(new(data.ToArray(), WebSocketMessageType.Text), ct);
    }
}
