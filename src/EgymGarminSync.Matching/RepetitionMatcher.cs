using System.Text.Json;

namespace EgymGarminSync.Matching;

/// <summary>Compare repetition counts independently of exercise order; preserve individual set values.</summary>
public static class RepetitionMatcher
{
    public static IReadOnlyList<RepetitionComparison> Compare(
        IReadOnlyCollection<EgymExerciseReps> egym,
        IReadOnlyCollection<GarminExerciseReps> garmin,
        int expectedSets = 2)
    {
        ArgumentNullException.ThrowIfNull(egym);
        ArgumentNullException.ThrowIfNull(garmin);
        if (expectedSets < 1) throw new ArgumentOutOfRangeException(nameof(expectedSets));
        var results = new List<RepetitionComparison>();
        foreach (var exercise in egym)
        {
            if (!ExerciseIdentityMapping.TryGetGarminKey(exercise.EgymActivityId, out var key))
            {
                results.Add(new(exercise.EgymActivityId, exercise.Repetitions, [], RepetitionStatus.UnmappedExercise));
                continue;
            }
            var sets = garmin.Where(s => s.IsActiveStrength && s.Category == key.Category &&
                                                  s.ExerciseName == key.ExerciseName).ToArray();
            if (sets.Length != expectedSets || exercise.Repetitions.Count != expectedSets ||
                exercise.Repetitions.Any(x => x is null or < 0) ||
                sets.Any(x => x.Repetitions is null or < 0))
            {
                results.Add(new(exercise.EgymActivityId, exercise.Repetitions,
                    sets.Select(x => x.Repetitions).ToArray(), RepetitionStatus.MissingOrInvalidSets));
                continue;
            }

            // Circuit set ordering is not an identity guarantee across two systems.
            // Compare per-exercise repetition multisets, not sequence numbers.
            var source = exercise.Repetitions.Select(x => x!.Value).Order().ToArray();
            var target = sets.Select(x => x.Repetitions!.Value).Order().ToArray();
            results.Add(new(exercise.EgymActivityId, exercise.Repetitions,
                sets.Select(x => x.Repetitions).ToArray(),
                source.SequenceEqual(target) ? RepetitionStatus.Equal : RepetitionStatus.Different));
        }
        return results;
    }
}

public sealed record EgymExerciseReps(long EgymActivityId, IReadOnlyList<int?> Repetitions);
public sealed record GarminExerciseReps(string Category, string? ExerciseName, int? Repetitions, bool IsActiveStrength);
public enum RepetitionStatus { Equal, Different, UnmappedExercise, MissingOrInvalidSets }
public sealed record RepetitionComparison(long EgymActivityId, IReadOnlyList<int?> EgymReps,
    IReadOnlyList<int?> GarminReps, RepetitionStatus Status);

public static class RepetitionJsonNormalizer
{
    public static IReadOnlyList<EgymExerciseReps> Egym(JsonElement workout)
    {
        var result = new List<EgymExerciseReps>();
        foreach (var exercise in workout.GetProperty("exercises").EnumerateArray())
        {
            var id = exercise.GetProperty("activity").GetProperty("activityId").GetInt64();
            if (id == 31) continue;
            var reps = exercise.GetProperty("sets").EnumerateArray()
                .Select(s => ReadReps(s, "numberOfReps")).ToArray();
            result.Add(new(id, reps));
        }
        return result;
    }

    public static IReadOnlyList<GarminExerciseReps> Garmin(JsonElement response)
    {
        var result = new List<GarminExerciseReps>();
        foreach (var set in response.GetProperty("exerciseSets").EnumerateArray())
        {
            if (set.GetProperty("setType").GetString() != "ACTIVE") continue;
            var identities = set.GetProperty("exercises").EnumerateArray().ToArray();
            if (identities.Length == 0) throw new JsonException("Garmin ACTIVE set lacks exercises.");
            var first = identities[0];
            var category = first.GetProperty("category").GetString()
                ?? throw new JsonException("Garmin set lacks category.");
            var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            if (category == "INDOOR_BIKE") continue;
            if (identities.Skip(1).Any(x => x.GetProperty("category").GetString() != category ||
                (x.TryGetProperty("name", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null) != name))
                throw new JsonException("Conflicting exercise identities within Garmin set.");
            result.Add(new(category, name, ReadReps(set, "repetitionCount"), true));
        }
        return result;
    }

    private static int? ReadReps(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new JsonException($"Invalid repetition field: {key}.");
        return number;
    }
}
