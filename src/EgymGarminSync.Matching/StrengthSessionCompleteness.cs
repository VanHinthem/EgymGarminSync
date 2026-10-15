namespace EgymGarminSync.Matching;

/// <summary>Pure read-only structural checks. No changes to source records.</summary>
public static class StrengthSessionCompleteness
{
    // Currently observed full EGYM circuit: eight machines and two rounds.
    // Caller must explicitly choose the expected circuit shape.
    public static CompletenessReport Check(
        IReadOnlyCollection<EgymExerciseSetCount> egym,
        IReadOnlyCollection<GarminExerciseSetCount> garmin,
        IReadOnlyCollection<long> expectedEgymIds,
        int expectedSetsPerExercise)
    {
        ArgumentNullException.ThrowIfNull(egym);
        ArgumentNullException.ThrowIfNull(garmin);
        ArgumentNullException.ThrowIfNull(expectedEgymIds);
        if (expectedSetsPerExercise < 1) throw new ArgumentOutOfRangeException(nameof(expectedSetsPerExercise));
        if (expectedEgymIds.Count == 0 || expectedEgymIds.Distinct().Count() != expectedEgymIds.Count)
            throw new ArgumentException("Expected exercise identities must be unique and non-empty.", nameof(expectedEgymIds));
        if (egym.Any(x => x.SetCount < 0) || garmin.Any(x => x.SetCount < 0))
            throw new ArgumentException("Set counts cannot be negative.");

        var issues = new List<CompletenessIssue>();
        foreach (var id in expectedEgymIds)
        {
            var source = egym.Where(x => x.EgymActivityId == id).ToArray();
            if (source.Length != 1 || source[0].SetCount != expectedSetsPerExercise)
                issues.Add(new(CompletenessSide.Egym, id, source.Sum(x => x.SetCount), expectedSetsPerExercise,
                    source.Length != 1 ? "MissingOrDuplicateExercise" : "UnexpectedSetCount"));

            // Unknown mapping must be reported as incomplete rather than guessed.
            if (!ExerciseIdentityMapping.TryGetGarminKey(id, out var key))
            {
                issues.Add(new(CompletenessSide.Mapping, id, 0, expectedSetsPerExercise, "UnknownExercise"));
                continue;
            }

            var records = garmin.Where(x => x.IsActiveStrength &&
                x.Category == key.Category && x.ExerciseName == key.ExerciseName).ToArray();
            var count = records.Sum(x => x.SetCount);
            if (count != expectedSetsPerExercise)
                issues.Add(new(CompletenessSide.Garmin, id, count, expectedSetsPerExercise, "UnexpectedSetCount"));
        }
        var extraEgym = egym.Where(x => !expectedEgymIds.Contains(x.EgymActivityId)).ToArray();
        foreach (var extra in extraEgym)
            issues.Add(new(CompletenessSide.Egym, extra.EgymActivityId, extra.SetCount, 0, "UnexpectedExercise"));

        // Any extra ACTIVE strength records not mapped to expected EGYM machines
        // must be reviewed; bike/REST data stays outside this calculation.
        var expectedKeys = expectedEgymIds
            .Select(id => ExerciseIdentityMapping.TryGetGarminKey(id, out var key) ? key : null)
            .Where(key => key is not null).ToHashSet();
        foreach (var extra in garmin.Where(x => x.IsActiveStrength &&
                     !expectedKeys.Contains(new GarminExerciseKey(x.Category, x.ExerciseName))))
            issues.Add(new(CompletenessSide.Garmin, null, extra.SetCount, 0, "UnexpectedExercise"));

        return new(issues.Count == 0, issues);
    }
}

public static class ExerciseIdentityMapping
{
    private static readonly IReadOnlyDictionary<long, GarminExerciseKey> Keys =
        new Dictionary<long, GarminExerciseKey>
        {
            [333] = new("BENCH_PRESS", null),
            [334] = new("ROW", "SEATED_CABLE_ROW"),
            [581] = new("SQUAT", "LEG_PRESS"),
            [330] = new("CRUNCH", "WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER"),
            [335] = new("PULL_UP", "LAT_PULLDOWN"),
            [329] = new("CRUNCH", "WEIGHTED_LEG_EXTENSIONS"),
            [331] = new("HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION"),
            [332] = new("LEG_CURL", "WEIGHTED_LEG_CURL")
        };

    public static bool TryGetGarminKey(long egymId, out GarminExerciseKey key) => Keys.TryGetValue(egymId, out key!);
}

public sealed record EgymExerciseSetCount(long EgymActivityId, int SetCount);
public sealed record GarminExerciseSetCount(string Category, string? ExerciseName, int SetCount, bool IsActiveStrength);
public enum CompletenessSide { Egym, Garmin, Mapping }
public sealed record CompletenessIssue(CompletenessSide Side, long? EgymActivityId, int ActualSets, int ExpectedSets, string Reason);
public sealed record CompletenessReport(bool IsComplete, IReadOnlyList<CompletenessIssue> Issues);
