namespace EgymGarminSync.Matching;

/// <summary>
/// Safe review-only report. A plausible session link is not permission to update Garmin.
/// </summary>
public static class ReadOnlyComparisonReport
{
    public static ComparisonReport Build(
        SessionMatch session,
        IReadOnlyCollection<EgymExerciseSetCount> egymCounts,
        IReadOnlyCollection<GarminExerciseSetCount> garminCounts,
        IReadOnlyCollection<EgymExerciseWeight> egymWeights,
        IReadOnlyCollection<GarminRecordedSet> garminWeights,
        IReadOnlyCollection<long> expectedMachineIds,
        int expectedSetsPerMachine = 2)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Do not compare weights across ambiguous or disputed sessions.
        if (session.Status != SessionMatchStatus.Matched || session.GarminActivityId is null)
            return new(session, null, [], false, "Session not unambiguously matched");

        var completeness = StrengthSessionCompleteness.Check(
            egymCounts, garminCounts, expectedMachineIds, expectedSetsPerMachine);
        if (!completeness.IsComplete)
            return new(session, completeness, [], false, "Incomplete circuit: no weight comparison");

        if (egymWeights.Count != expectedMachineIds.Count ||
            egymWeights.Select(x => x.EgymActivityId).Distinct().Count() != expectedMachineIds.Count ||
            expectedMachineIds.Any(id => egymWeights.Count(x => x.EgymActivityId == id) != 1))
            return new(session, completeness, [], false, "Missing or duplicate EGYM weight identities");

        var differences = StrengthWeightMatcher.Compare(egymWeights, garminWeights);
        var reviewable = differences.All(x => x.Status is MatchStatus.Equal or MatchStatus.Different);
        return new(session, completeness, differences, reviewable,
            reviewable ? "Read-only comparison available; weights require unit/negative-method verification" :
                "Unmapped, missing or ambiguous recorded weights");
    }
}

public sealed record ComparisonReport(
    SessionMatch Session,
    CompletenessReport? Completeness,
    IReadOnlyList<WeightDifference> Differences,
    bool HasReviewableDifferences,
    string Reason);
