namespace Quacktion.App.Interfaces;
public sealed class DeviceNotificationRequest
{
    public string NotifyId { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerAccountId { get; set; } = "";
    public string Kind { get; set; } = "quack";
    public string? SpokenMessage { get; set; }
    public int ExpiresInSeconds { get; set; } = 60;
    public DateTime DueAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
    public string? QuacktionId { get; set; }
    public string? QuacktionStatus { get; set; }
    public string? MeetingId { get; set; }
    public long CalendarRevision { get; set; }
}
public sealed record NotificationResult(string NotifyId, string Status);
public interface IDeviceNotificationService
{
    Task<NotificationResult> NotifyAsync(string deviceId, DeviceNotificationRequest request, CancellationToken cancellationToken);
}
