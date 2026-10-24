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
    public async Task<string> AttachAsync(Device device, string sessionId, AudioNegotiation negotiation, CancellationToken ct)
    {
        Guard.Input(negotiation.ProtocolVersion == 1, "unsupported_protocol_version");
        Guard.Input(negotiation.Input.SampleRate > 0 && negotiation.Output.SampleRate > 0 && negotiation.Input.Channels > 0 && negotiation.Output.Channels > 0, "invalid_audio_format");
        Guard.Input(settings.Value.AllowedInputFormats.Contains(negotiation.Input) && settings.Value.AllowedOutputFormats.Contains(negotiation.Output), "unsupported_audio_format");
        var session = Guard.Found(await sessions.FindAsync(x => x.Id == sessionId && x.OwnerAccountId == device.OwnerAccountId && x.DeviceId == device.Id, ct));
        var current = Guard.Found(await devices.GetAsync(device.Id, ct));
        Guard.State(current.ActiveSessionId == sessionId && session.Status == SessionStatus.Active && session.AgentSessionId is not null, "session_not_active");
        await adapter.NegotiateAsync(session.AgentSessionId!, negotiation, ct); return session.AgentSessionId!;
    }
    public Task SendAsync(string agentSessionId, ReadOnlyMemory<byte> frame, CancellationToken ct) => adapter.SendAudioAsync(agentSessionId, frame, ct);
    public IAsyncEnumerable<ProviderEvent> ReadAsync(string agentSessionId, CancellationToken ct) => adapter.ReadEventsAsync(agentSessionId, ct);
}
