using System.Collections.Concurrent;
using Quacktion.App.Interfaces;

namespace Quacktion.Data.Audio;

public sealed class SessionAudioConnectionManager : ISessionAudioConnectionManager
{
    private readonly ConcurrentDictionary<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task>> connections = new();

    public IDisposable Register(string sessionId, Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendControl)
    {
        if (!connections.TryAdd(sessionId, sendControl))
            throw new InvalidOperationException("A session audio connection is already registered.");
        return new Registration(connections, sessionId, sendControl);
    }

    public async Task<bool> SendControlAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!connections.TryGetValue(sessionId, out var send)) return false;
        await send(data, ct);
        return true;
    }

    private sealed class Registration(
        ConcurrentDictionary<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task>> connections,
        string sessionId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send
    ) : IDisposable
    {
        public void Dispose() =>
            ((ICollection<KeyValuePair<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task>>>)connections)
            .Remove(new(sessionId, send));
    }
}
