namespace EgymGarminSync.Matching;

/// <summary>Read-only, pure comparison of exercise weights. No Garmin or EGYM network calls.</summary>
public static class StrengthWeightMatcher
{
    // Provisional mappings from inspected live data. Validate before any writes.
    private static readonly IReadOnlyDictionary<long, GarminExerciseKey> Mapping =
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

    public static IReadOnlyList<WeightDifference> Compare(
        IReadOnlyCollection<EgymExerciseWeight> egym,
        IReadOnlyCollection<GarminRecordedSet> garmin)
    {
        ArgumentNullException.ThrowIfNull(egym);
        ArgumentNullException.ThrowIfNull(garmin);

        var results = new List<WeightDifference>();

        // Never infer identity from sequence or exercise count.
        foreach (var source in egym)
        {
            if (!Mapping.TryGetValue(source.EgymActivityId, out var key))
            {
                results.Add(new(source.EgymActivityId, source.WeightKg, null, null, MatchStatus.UnmappedExercise));
                continue;
            }

            var candidates = garmin.Where(x => x.IsActiveStrength &&
                string.Equals(x.Category, key.Category, StringComparison.Ordinal) &&
                string.Equals(x.ExerciseName, key.ExerciseName, StringComparison.Ordinal)).ToArray();

            if (candidates.Length == 0)
            {
                results.Add(new(source.EgymActivityId, source.WeightKg, null, key, MatchStatus.MissingGarminSet));
                continue;
            }

            // Two circuit rounds may have two identical sets, but conflicting weights
            // must be reviewed, not silently overwritten.
            var uniqueWeights = candidates.Select(x => x.WeightGrams).Distinct().ToArray();
            if (uniqueWeights.Length != 1 || uniqueWeights[0] is null)
            {
                results.Add(new(source.EgymActivityId, source.WeightKg, null, key, MatchStatus.AmbiguousGarminWeight));
                continue;
            }

            var weightKg = uniqueWeights[0]!.Value / 1000m;
            var status = weightKg == source.WeightKg ? MatchStatus.Equal : MatchStatus.Different;
            results.Add(new(source.EgymActivityId, source.WeightKg, weightKg, key, status));
        }

        return results;
    }
}

public sealed record GarminExerciseKey(string Category, string? ExerciseName);
public sealed record EgymExerciseWeight(long EgymActivityId, decimal WeightKg);
public sealed record GarminRecordedSet(string Category, string? ExerciseName, decimal? WeightGrams, bool IsActiveStrength);
public enum MatchStatus { Equal, Different, UnmappedExercise, MissingGarminSet, AmbiguousGarminWeight }
public sealed record WeightDifference(
    long EgymActivityId, decimal EgymKg, decimal? GarminKg, GarminExerciseKey? GarminKey, MatchStatus Status)
{
    public decimal? DeltaKg => GarminKg is null ? null : EgymKg - GarminKg.Value;
}
