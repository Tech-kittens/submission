namespace Quacktion.App.Models;

public sealed class MeetingMemoryDocument : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string MeetingId { get; set; } = "";
    public string MeetingTitle { get; set; } = "";
    public DateTime MeetingDate { get; set; }
    public string MeetingType { get; set; } = "";
    public string Type { get; set; } = "";
    public string Content { get; set; } = "";
    public string[] ParticipantEmails { get; set; } = [];
    public string[] Topics { get; set; } = [];
    public string[] SourceSegmentIds { get; set; } = [];
    public float[] Embedding { get; set; } = [];
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimensions { get; set; }
    public string Generation { get; set; } = "";
    public bool Active { get; set; }
}

public sealed class PreparationPreferences : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public bool Enabled { get; set; }
    public bool MessagingOptIn { get; set; }
    public bool ClarificationsEnabled { get; set; }
    public bool GmailEnabled { get; set; }
    public int BriefingMinutes { get; set; } = 30;
    public string[] ExcludedCalendarIds { get; set; } = [];
    public string[] ExcludedEventIds { get; set; } = [];
    public int RetentionDays { get; set; } = 90;
}

public sealed class CalendarSyncState : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string GoogleAccountId { get; set; } = "";
    public string CalendarId { get; set; } = "primary";
    public string? SyncToken { get; set; }
    public DateTime? LastSnapshotAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? FailureCode { get; set; }
    public int Attempts { get; set; }
    public string? LeaseId { get; set; }
    public DateTime LeaseUntil { get; set; }
}

public sealed record PreparationAttendee(string Email, string? Name, string? ResponseStatus = null);
public sealed record ResearchSource(string Id, string Kind, string Reference, string? Excerpt = null, bool Verified = false);
public sealed record AttendeeResearch(string Email, string Summary, string[] SourceIds);
public sealed record PreviousMeetingContext(string MeetingId, string Summary, string[] SourceIds);
public sealed record PreparationRisk(string Description, string[] SourceIds);
public sealed record PreparationDecision(string Description, string[] SourceIds);
public sealed record PreparationCommitment(string Description, string[] SourceIds);
public sealed class PreparationResearch
{
    public string Summary { get; set; } = "";
    public string Objective { get; set; } = "";
    public List<string> KeyTalkingPoints { get; set; } = [];
    public List<AttendeeResearch> Attendees { get; set; } = [];
    public List<PreviousMeetingContext> PreviousMeetings { get; set; } = [];
    public List<PreparationDecision> EvidenceDecisions { get; set; } = [];
    public List<PreparationCommitment> Commitments { get; set; } = [];
    public List<string> Decisions { get; set; } = [];
    public List<string> OutstandingCommitments { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
    public List<PreparationRisk> Risks { get; set; } = [];
    public List<string> SuggestedAgenda { get; set; } = [];
    public List<string> SuggestedQuestions { get; set; } = [];
    public List<ResearchSource> Sources { get; set; } = [];
}
public sealed class PreparationQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime AskedAt { get; set; }
    public string? Answer { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public bool LateReply { get; set; }
}
public sealed record PreparationRevision(long Revision, DateTime At, string RequestId, PreparationResearch Research);
public sealed class MeetingPreparation : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string GoogleAccountId { get; set; } = "";
    public string CalendarId { get; set; } = "";
    public string CalendarEventId { get; set; } = "";
    public string? RecurringEventId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string Timezone { get; set; } = "UTC";
    public List<PreparationAttendee> Attendees { get; set; } = [];
    public string CalendarFingerprint { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public long CalendarRevision { get; set; } = 1;
    public PreparationResearch Research { get; set; } = new();
    public List<PreparationQuestion> Questions { get; set; } = [];
    public List<PreparationRevision> Revisions { get; set; } = [];
    public DateTime ResearchNextAttemptAt { get; set; }
    public DateTime ResearchLeaseUntil { get; set; }
    public string? ResearchLeaseId { get; set; }
    public int ResearchAttempts { get; set; }
    public string? ResearchFailureCode { get; set; }
    public string? LastRequestId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime BriefingDueAt { get; set; }
    public string BriefingStatus { get; set; } = "Pending";
    public DateTime? BriefingDeliveredAt { get; set; }
    public string? BriefingProviderMessageId { get; set; }
    // Embedded outbox: the state change and its delivery intent commit in one document.
    public List<PreparationEvent> Outbox { get; set; } = [];
    public string? LeaseId { get; set; }
    public DateTime LeaseUntil { get; set; }
    public DateTime ExpiresAt { get; set; }
}
public sealed class PreparationEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public long CalendarRevision { get; set; }
    public string? QuestionId { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? FailureCode { get; set; }
}
