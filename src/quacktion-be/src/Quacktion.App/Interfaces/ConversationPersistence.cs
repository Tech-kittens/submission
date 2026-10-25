using Quacktion.App.Models;
namespace Quacktion.App.Interfaces;
public interface IConversationPersistence
{
    Task AcquireAsync(string owner, string session, string lease, CancellationToken ct);
    Task<bool> RenewAsync(string owner, string session, string lease, CancellationToken ct);
    Task ReleaseAsync(string owner, string session, string lease, CancellationToken ct);
    Task<bool> IsLeasedAsync(string owner, string session, CancellationToken ct);
    Task RecordAsync(string owner, string session, string type, object payload, CancellationToken ct);
    Task<IReadOnlyList<ConversationEvent>> HistoryAsync(string owner, string session, long after, int limit, CancellationToken ct);
}
public interface IConversationContextService
{
    Task<string> BuildRestorationContextAsync(string conversationId, CancellationToken cancellationToken);
}
public interface IDeviceTransportPersistence
{
    Task AcquireAsync(Device device, string lease, string instanceId, CancellationToken ct);
    Task<bool> CheckpointAsync(Device device, string lease, CancellationToken ct);
    Task AudioReturnedAsync(Device device, string lease, CancellationToken ct);
    Task DisconnectAsync(Device device, string lease, CancellationToken ct);
}
