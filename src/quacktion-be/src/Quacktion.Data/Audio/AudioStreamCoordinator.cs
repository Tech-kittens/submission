using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
namespace Quacktion.Data.Audio;

public sealed class AudioSettings
{
    public AudioFormat[] AllowedInputFormats { get; set; } = [];
    public AudioFormat[] AllowedOutputFormats { get; set; } = [];
}
public sealed class AudioStreamCoordinator(ISessionRepo sessions, IDeviceRepo devices, IAudioProviderAdapter adapter, IOptions<AudioSettings> settings) : IAudioStreamCoordinator
{
    public Task<string> AttachAsync(Device device, string sessionId, AudioNegotiation negotiation, CancellationToken ct) =>
        AttachAsync(device.OwnerAccountId, device.Id, sessionId, negotiation, ct);

    public Task<string> AttachAsync(string owner, string sessionId, AudioNegotiation negotiation, CancellationToken ct) =>
        AttachAsync(owner, null, sessionId, negotiation, ct);

    private async Task<string> AttachAsync(string owner, string? deviceId, string sessionId, AudioNegotiation negotiation, CancellationToken ct)
    {
        Guard.Input(negotiation.ProtocolVersion == 1, "unsupported_protocol_version");
        Guard.Input(negotiation.Input.SampleRate > 0 && negotiation.Output.SampleRate > 0 && negotiation.Input.Channels > 0 && negotiation.Output.Channels > 0, "invalid_audio_format");
        Guard.Input(settings.Value.AllowedInputFormats.Contains(negotiation.Input) && settings.Value.AllowedOutputFormats.Contains(negotiation.Output), "unsupported_audio_format");
        var session = Guard.Found(deviceId is null
            ? await sessions.FindAsync(x => x.Id == sessionId && x.OwnerAccountId == owner, ct)
            : await sessions.FindAsync(x => x.Id == sessionId && x.OwnerAccountId == owner && x.DeviceId == deviceId, ct));
        if (deviceId is not null)
        {
            var current = Guard.Found(await devices.GetAsync(deviceId, ct));
            Guard.State(current.ActiveSessionId == sessionId, "session_not_active");
        }
        Guard.State(session.Status == SessionStatus.Active && session.AgentSessionId is not null, "session_not_active");
        await adapter.NegotiateAsync(session.AgentSessionId!, negotiation, ct); return session.AgentSessionId!;
    }
    public Task SendAsync(string agentSessionId, ReadOnlyMemory<byte> frame, CancellationToken ct) => adapter.SendAudioAsync(agentSessionId, frame, ct);
    public IAsyncEnumerable<ProviderEvent> ReadAsync(string agentSessionId, CancellationToken ct) => adapter.ReadEventsAsync(agentSessionId, ct);
}
