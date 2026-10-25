using Quacktion.App.Models;

namespace Quacktion.App.Services;

public static class MeetingScoring
{
    public static readonly IReadOnlyDictionary<string, double> BaseWeights = new Dictionary<string, double>
    {
        ["purposeAndFocus"] = 15, ["decisionEffectiveness"] = 15, ["actionItemQuality"] = 15,
        ["participationBalance"] = 10, ["communicationClarity"] = 10, ["collaborationQuality"] = 10,
        ["timeEfficiency"] = 10, ["meetingOutcomes"] = 10, ["followUpReadiness"] = 5
    };

    public static MeetingScorecard Calculate(MeetingType type, IEnumerable<MeetingAssessment> assessments)
    {
        var weights = new Dictionary<string, double>(BaseWeights);
        if (type == MeetingType.Brainstorming) { weights["decisionEffectiveness"] = 5; weights["collaborationQuality"] = 20; }
        if (type == MeetingType.Planning) { weights["actionItemQuality"] = 25; weights["participationBalance"] = 5; weights["timeEfficiency"] = 5; }
        if (type is MeetingType.Presentation or MeetingType.ClientMeeting or MeetingType.Interview)
        { weights["participationBalance"] = 5; weights["communicationClarity"] = 15; }
        if (type == MeetingType.Standup) { weights["decisionEffectiveness"] = 5; weights["followUpReadiness"] = 15; }

        var byCategory = assessments.Where(a => weights.ContainsKey(a.Category)).GroupBy(a => a.Category)
            .ToDictionary(g => g.Key, g => g.First());
        var applicable = weights.Keys.Where(k => !byCategory.TryGetValue(k, out var a) || !a.NotApplicable).ToList();
        if (applicable.Count == 0 || applicable.Any(k => !byCategory.TryGetValue(k, out var a)
            || a.Score is not >= 0 and <= 10 || a.EvidenceSegmentIds.Count == 0)) return new MeetingScorecard();
        var scored = applicable.Select(k => byCategory[k]).ToList();
        var total = scored.Sum(a => weights[a.Category]);
        var applied = scored.ToDictionary(a => a.Category, a => weights[a.Category] * 100 / total);
        var value = Math.Round(scored.Sum(a => a.Score!.Value / 10 * applied[a.Category]), MidpointRounding.AwayFromZero);
        var classification = value switch
        {
            >= 90 => "Excellent", >= 80 => "Very Effective", >= 70 => "Effective",
            >= 60 => "Mixed Results", >= 40 => "Needs Improvement", _ => "Limited Effectiveness"
        };
        return new MeetingScorecard { OverallScore = value, Classification = classification, AppliedWeights = applied };
    }
}
