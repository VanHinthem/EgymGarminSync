using System.Text.Json;

namespace EgymGarminSync.Matching;

/// <summary>Validates the undocumented Garmin replace-all payload contract without sending HTTP.</summary>
public static class GarminExerciseSetsWriteContract
{
    public static void Validate(string json, long activityId)
    {
        if (activityId <= 0) throw new ArgumentOutOfRangeException(nameof(activityId));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !HasActivityId(root, activityId) ||
            !root.TryGetProperty("exerciseSets", out var sets) ||
            sets.ValueKind != JsonValueKind.Array || sets.GetArrayLength() != 36)
            throw new InvalidOperationException("Invalid root activity ID or 36-set payload.");
        foreach (var set in sets.EnumerateArray())
        {
            if (set.ValueKind != JsonValueKind.Object || !HasActivityId(set, activityId))
                throw new InvalidOperationException("Set activity ID missing or mismatched.");
            if (!set.TryGetProperty("setType", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("ACTIVE" or "REST"))
                throw new InvalidOperationException("Set type missing or unknown.");
            if (!set.TryGetProperty("startTime", out var time) ||
                time.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(time.GetString(), out _))
                throw new InvalidOperationException("Set startTime missing or invalid.");
            if (type.GetString() != "ACTIVE") continue;
            if (!set.TryGetProperty("exercises", out var exercises) ||
                exercises.ValueKind != JsonValueKind.Array ||
                exercises.GetArrayLength() == 0)
                throw new InvalidOperationException("ACTIVE set exercises missing.");
            foreach (var exercise in exercises.EnumerateArray())
            {
                if (exercise.ValueKind != JsonValueKind.Object || !HasActivityId(exercise, activityId))
                    throw new InvalidOperationException("Nested exercise activity ID missing.");
            }
        }
    }

    /// <summary>Schema inventory only: never prints personal exercise record contents.</summary>
    public static GarminSetSchemaInventory Inspect(string json, long activityId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sets = root.GetProperty("exerciseSets");
        if (sets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("exerciseSets is not an array.");
        int setMissing = 0, setWrong = 0, exerciseMissing = 0, exerciseWrong = 0;
        int timeMissing = 0, timeInvalid = 0, active = 0, rest = 0, nested = 0;
        foreach (var set in sets.EnumerateArray())
        {
            CountId(set, activityId, ref setMissing, ref setWrong);
            if (set.TryGetProperty("setType", out var type) && type.ValueKind == JsonValueKind.String)
            {
                if (type.GetString() == "ACTIVE") active++;
                if (type.GetString() == "REST") rest++;
            }
            if (!set.TryGetProperty("startTime", out var time)) timeMissing++;
            else if (time.ValueKind != JsonValueKind.String ||
                     !DateTimeOffset.TryParse(time.GetString(), out _)) timeInvalid++;
            if (!set.TryGetProperty("exercises", out var exercises) ||
                exercises.ValueKind != JsonValueKind.Array) continue;
            foreach (var exercise in exercises.EnumerateArray())
            {
                nested++;
                CountId(exercise, activityId, ref exerciseMissing, ref exerciseWrong);
            }
        }
        var rootMissing = !root.TryGetProperty("activityId", out _);
        return new(rootMissing, !rootMissing && !HasActivityId(root, activityId),
            sets.GetArrayLength(), active, rest, nested, setMissing, setWrong,
            exerciseMissing, exerciseWrong, timeMissing, timeInvalid);
    }

    private static void CountId(JsonElement item, long activityId, ref int missing, ref int wrong)
    {
        if (!item.TryGetProperty("activityId", out _)) missing++;
        else if (!HasActivityId(item, activityId)) wrong++;
    }

    private static bool HasActivityId(JsonElement item, long activityId) =>
        item.TryGetProperty("activityId", out var id) &&
        id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) &&
        number == activityId;
}

public sealed record GarminSetSchemaInventory(
    bool RootIdMissing, bool RootIdWrong, int SetCount, int ActiveCount, int RestCount,
    int NestedExerciseCount, int SetIdMissing, int SetIdWrong,
    int ExerciseIdMissing, int ExerciseIdWrong, int StartTimeMissing, int StartTimeInvalid);
