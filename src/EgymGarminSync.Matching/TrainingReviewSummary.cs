namespace EgymGarminSync.Matching;

/// <summary>Independent, read-only review dimensions. No state implies write approval.</summary>
public static class TrainingReviewSummary
{
    public static TrainingReviewStatus Build(
        CompletenessReport completeness,
        IReadOnlyList<RepetitionComparison> repetitions,
        IReadOnlyList<SetWeightReview> weights,
        IReadOnlyCollection<long> expectedIds)
    {
        ArgumentNullException.ThrowIfNull(completeness);
        var extra = completeness.Issues.Count(x => x.Side == CompletenessSide.Garmin &&
            x.Reason == "UnexpectedExercise");
        var mapped = completeness.Issues.All(x => x.Side == CompletenessSide.Garmin &&
            x.Reason == "UnexpectedExercise");
        var repCoverage = mapped && repetitions.Count == expectedIds.Count &&
            expectedIds.All(id => repetitions.Count(r => r.EgymActivityId == id) == 1);
        var weightCoverage = mapped && weights.Count == expectedIds.Count &&
            expectedIds.All(id => weights.Count(w => w.ExerciseId == id) == 1);
        return new(mapped, extra,
            repCoverage ? repetitions.Count(x => x.Status == RepetitionStatus.Different) : null,
            weightCoverage ? weights.Count(x => !x.Equal) : null,
            repCoverage && repetitions.All(x => x.Status == RepetitionStatus.Equal),
            weightCoverage && weights.All(x => x.Equal));
    }
}
public sealed record TrainingReviewStatus(
    bool ExpectedMappedSetsComplete,
    int ExtraGarminRecords,
    int? RepetitionDifferences,
    int? ExerciseWeightDifferences,
    bool RepetitionsEqual,
    bool SetWeightsEqual);
