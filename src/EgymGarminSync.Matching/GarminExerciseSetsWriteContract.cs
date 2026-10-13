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

    private static bool HasActivityId(JsonElement item, long activityId) =>
        item.TryGetProperty("activityId", out var id) &&
        id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) &&
        number == activityId;
}
