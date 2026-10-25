using Quacktion.App.Models;
using Quacktion.App.Services;
using Xunit;

namespace Quacktion.App.Tests;

public sealed class MeetingScoringTests
{
    [Fact]
    public void AllEvidenceBackedScoresUsePublishedWeights()
    {
        var categories = MeetingScoring.BaseWeights.Keys.Select(category => new MeetingAssessment
        { Category = category, Score = category == "purposeAndFocus" ? 10 : 5, EvidenceSegmentIds = ["segment-1"] });
        var score = MeetingScoring.Calculate(MeetingType.General, categories);
        Assert.Equal(58, score.OverallScore);
        Assert.Equal("Needs Improvement", score.Classification);
    }

    [Fact]
    public void MissingEvidenceMakesOverallScoreUnavailable()
    {
        var score = MeetingScoring.Calculate(MeetingType.Presentation,
        [new MeetingAssessment { Category = "purposeAndFocus", Score = 8, EvidenceSegmentIds = ["s1"] },
         new MeetingAssessment { Category = "decisionEffectiveness", Score = 0 },
         new MeetingAssessment { Category = "participationBalance", Score = null }]);
        Assert.Null(score.OverallScore);
    }

    [Fact]
    public void InapplicableCategoriesRedistributeWeights()
    {
        var assessments = MeetingScoring.BaseWeights.Keys.Select(category => new MeetingAssessment
        { Category = category, Score = category == "purposeAndFocus" ? 8 : null,
            EvidenceSegmentIds = category == "purposeAndFocus" ? ["s1"] : [],
            NotApplicable = category != "purposeAndFocus" });
        var score = MeetingScoring.Calculate(MeetingType.Presentation, assessments);
        Assert.Equal(80, score.OverallScore);
        Assert.Equal(100, score.AppliedWeights["purposeAndFocus"]);
    }

    [Fact]
    public void NoSupportedEvidenceHasUnavailableScore()
    {
        var score = MeetingScoring.Calculate(MeetingType.General, []);
        Assert.Null(score.OverallScore);
        Assert.Equal("Unavailable", score.Classification);
    }
}
