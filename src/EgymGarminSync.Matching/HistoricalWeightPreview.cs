namespace EgymGarminSync.Matching;

/// <summary>Presentation-only draft. Never a Garmin update command or authorization.</summary>
public static class HistoricalWeightPreview
{
    public static IReadOnlyList<ProposedWeightChange> Build(ComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Session.Status != SessionMatchStatus.Matched ||
            !report.HasReviewableDifferences || report.Completeness?.IsComplete != true ||
            report.Differences.Count != 8)
            return [];
        if (report.Differences.Select(x => x.EgymActivityId).Distinct().Count() != 8 ||
            report.Differences.Any(x => x.Status is not (MatchStatus.Different or MatchStatus.Equal)))
            return [];
        return report.Differences.Where(x => x.Status == MatchStatus.Different)
            .OrderBy(x => x.EgymActivityId)
            .Select(x => new ProposedWeightChange(x.EgymActivityId, x.GarminKg!.Value,
                x.EgymKg, x.DeltaKg!.Value)).ToArray();
    }
}
public sealed record ProposedWeightChange(long ExerciseId, decimal GarminKg, decimal EgymKg, decimal DifferenceKg);
