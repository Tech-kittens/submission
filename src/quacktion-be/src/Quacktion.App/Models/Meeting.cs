using System.Text.Json.Serialization;

namespace Quacktion.App.Models;

public sealed class Meeting : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string? PreparationId { get; set; }
    public string[] ParticipantEmails { get; set; } = [];
    public string? MemoryGeneration { get; set; }
    public DateTime? MemoryIndexedAt { get; set; }
    public DateTime? MemoryNextAttemptAt { get; set; }
    public int MemoryAttempts { get; set; }
    public string? MemoryFailureCode { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? EndedAt { get; set; }
    public MeetingType Type { get; set; } = MeetingType.General;
    public MeetingStatus Status { get; set; } = MeetingStatus.Active;
    public DateTime? ProcessedAt { get; set; }
    public string? AnalysisFailureCode { get; set; }
    public int AnalysisAttemptCount { get; set; }
    public DateTime? NextAnalysisAttemptAt { get; set; }
    public DateTime? LastInsightAttemptAt { get; set; }
    public bool RecordingConsent { get; set; }
    public bool RecordingEnabled { get; set; }
    public string RecordingStatus { get; set; } = "Unavailable";
    public long? RecordingBytes { get; set; }
    public string FinalTranscriptStatus { get; set; } = "Pending";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeetingType { General, Standup, Planning, Brainstorming, DecisionMaking, Retrospective, ClientMeeting, Presentation, Interview }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeetingStatus { Created, Active, Ending, Processing, Completed, Failed, Cancelled, Interrupted }

public sealed class MeetingSegment : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MeetingId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public string BootId { get; set; } = "";
    public long StartUptimeMs { get; set; }
    public long? StartMs { get; set; }
    public string ClockSource { get; set; } = "none";
    public int PreRollMs { get; set; }
    public bool Backlog { get; set; }
    public string? ContinuesSegmentId { get; set; }
    public int Frames { get; set; }
    public int Bytes { get; set; }
    public string Reason { get; set; } = "";
    public string AudioHash { get; set; } = "";
    public string? Transcript { get; set; }
    public string? FailureCode { get; set; }
    public string? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public long? EndMs { get; set; }
    public bool IsFinal { get; set; }
}

public sealed class MeetingTranscriptTurn : Entity
{
    public string MeetingId { get; set; } = "";
    public string OwnerAccountId { get; set; } = "";
    public string Text { get; set; } = "";
    public string? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
    public int ChunkIndex { get; set; }
    public bool IsFinal { get; set; } = true;
}

public sealed class MeetingAnalysis : Entity
{
    public string MeetingId { get; set; } = "";
    public string OwnerAccountId { get; set; } = "";
    public int AnalysisVersion { get; set; } = 1;
    public string Summary { get; set; } = "";
    public List<MeetingFinding> Decisions { get; set; } = [];
    public List<MeetingFinding> ActionItems { get; set; } = [];
    public List<MeetingFinding> Risks { get; set; } = [];
    public List<MeetingFinding> UnresolvedQuestions { get; set; } = [];
    public List<MeetingFinding> SuggestedQuacktions { get; set; } = [];
    public List<MeetingAssessment> Assessments { get; set; } = [];
    public MeetingScorecard? Scorecard { get; set; }
    public MeetingMetrics Metrics { get; set; } = new();
    public DateTime AnalysedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MeetingMetrics
{
    public long? DurationMs { get; set; }
    public int TranscriptSegments { get; set; }
    public int? Speakers { get; set; }
    public int ConfirmedDecisions { get; set; }
    public int ActionItems { get; set; }
    public int UnresolvedQuestions { get; set; }
    public int Risks { get; set; }
    public double? ActionItemsWithOwnersPercent { get; set; }
    public double? ActionItemsWithDeadlinesPercent { get; set; }
}

public sealed class MeetingInsight : Entity
{
    public string MeetingId { get; set; } = "";
    public string OwnerAccountId { get; set; } = "";
    public string FirstSegmentId { get; set; } = "";
    public string LastSegmentId { get; set; } = "";
    public List<string> SegmentIds { get; set; } = [];
    public string Summary { get; set; } = "";
    public List<MeetingFinding> Decisions { get; set; } = [];
    public List<MeetingFinding> ActionItems { get; set; } = [];
    public List<MeetingFinding> Risks { get; set; } = [];
    public List<MeetingFinding> UnresolvedQuestions { get; set; } = [];
}

public sealed class MeetingFinding
{
    public string Text { get; set; } = "";
    public string? OwnerSpeakerId { get; set; }
    public string? Deadline { get; set; }
    public string Status { get; set; } = "proposed";
    public List<string> EvidenceSegmentIds { get; set; } = [];
}

public sealed class MeetingAssessment
{
    public string Category { get; set; } = "";
    public bool NotApplicable { get; set; }
    public double? Score { get; set; }
    public string Explanation { get; set; } = "";
    public List<string> EvidenceSegmentIds { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
}

public sealed class MeetingScorecard
{
    public double? OverallScore { get; set; }
    public string Classification { get; set; } = "Unavailable";
    public Dictionary<string, double> AppliedWeights { get; set; } = [];
}

public sealed class MeetingEvent : Entity
{
    public string OwnerAccountId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MeetingId { get; set; } = "";
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}
