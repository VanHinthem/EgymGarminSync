using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class TrainingReviewSummaryTests
{
    [Fact]
    public void ExtraUnknownDoesNotHideMappedDifferences()
    {
        var report = TrainingReviewSummary.Build(
            new CompletenessReport(false, [new CompletenessIssue(CompletenessSide.Garmin, null, 1, 0, "UnexpectedExercise")]),
            [new RepetitionComparison(333, [15, 15], [20, 15], RepetitionStatus.Different)],
            [new SetWeightReview(333, [22m, 22m], [22m, 22m], true)],
            [333]);
        Assert.True(report.ExpectedMappedSetsComplete);
        Assert.Equal(1, report.ExtraGarminRecords);
        Assert.Equal(1, report.RepetitionDifferences);
        Assert.Equal(0, report.ExerciseWeightDifferences);
    }

    [Fact]
    public void MissingExpectedSetKeepsComparisonUnknown()
    {
        var report = TrainingReviewSummary.Build(
            new CompletenessReport(false, [new CompletenessIssue(CompletenessSide.Garmin, 333, 1, 2, "UnexpectedSetCount")]),
            [], [], [333]);
        Assert.False(report.ExpectedMappedSetsComplete);
        Assert.Null(report.RepetitionDifferences);
        Assert.Null(report.ExerciseWeightDifferences);
    }

    [Fact]
    public void VaryingSourceWeightsProduceOneExerciseDifference()
    {
        var report = TrainingReviewSummary.Build(
            new CompletenessReport(true, []),
            [new RepetitionComparison(329, [20, 20], [20, 20], RepetitionStatus.Equal)],
            [new SetWeightReview(329, [29m, 28m], [23m, 23m], false)],
            [329]);
        Assert.True(report.ExpectedMappedSetsComplete);
        Assert.Equal(0, report.RepetitionDifferences);
        Assert.Equal(1, report.ExerciseWeightDifferences);
    }
}
