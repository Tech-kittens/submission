namespace Quacktion.App.Models;

public sealed class DeviceNotification : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Kind { get; set; } = "quack";
    public string? SpokenMessage { get; set; }
    public string? QuacktionId { get; set; }
    public string? QuacktionStatus { get; set; }
    public string? MeetingId { get; set; }
    public long CalendarRevision { get; set; }
    public string Status { get; set; } = "Scheduled";
    public DateTime DueAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime RetainUntil { get; set; }
    public int ExpiresInSeconds { get; set; } = 60;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime LeaseUntil { get; set; }
    public string? LeaseId { get; set; }
    public string? ConnectionId { get; set; }
    public DateTime? SentAt { get; set; }
    public bool Spoken { get; set; }
    public string? Outcome { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public long? AtMs { get; set; }
    public long? AtUptimeMs { get; set; }
    public string? ClockSource { get; set; }
    public DateTime? OfferExpiresAt { get; set; }
    public string? OfferSessionId { get; set; }
    public string? OfferResolution { get; set; }
}
