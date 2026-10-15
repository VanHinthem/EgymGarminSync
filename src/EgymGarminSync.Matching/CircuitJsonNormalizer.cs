using System.Globalization;
using System.Text.Json;

namespace EgymGarminSync.Matching;

/// <summary>Converts verified API JSON into read-only circuit metrics; invalid data fails closed.</summary>
public static class CircuitJsonNormalizer
{
    public static IReadOnlyList<EgymExerciseSetCount> EgymCounts(JsonElement workout)
    {
        var exercises = RequireArray(workout, "exercises");
        var result = new List<EgymExerciseSetCount>();
        foreach (var exercise in exercises.EnumerateArray())
        {
            if (!exercise.TryGetProperty("activity", out var activity) ||
                !activity.TryGetProperty("activityId", out var idField) ||
                !TryLong(idField, out var id))
                throw new JsonException("EGYM exercise lacks a valid activity ID.");
            if (id == 31) continue; // Observed bike ID, not part of strength circuit.
            var sets = RequireArray(exercise, "sets");
            result.Add(new(id, sets.GetArrayLength()));
        }
        return result;
    }

    public static IReadOnlyList<GarminExerciseSetCount> GarminCounts(JsonElement response)
    {
        var sets = RequireArray(response, "exerciseSets");
        var result = new List<GarminExerciseSetCount>();
        foreach (var set in sets.EnumerateArray())
        {
            if (!set.TryGetProperty("setType", out var type))
                throw new JsonException("Garmin set lacks setType.");
            var setType = type.GetString();
            if (setType == "REST") continue;
            if (setType != "ACTIVE") throw new JsonException("Unknown Garmin setType.");
            var exercises = RequireArray(set, "exercises");
            if (exercises.GetArrayLength() == 0)
                throw new JsonException("Active Garmin set lacks exercise identity.");
            // Repeated identical names within one set record are NOT additional sets.
            var first = exercises[0];
            var key = ReadGarminKey(first);
            foreach (var item in exercises.EnumerateArray().Skip(1))
                if (ReadGarminKey(item) != key)
                    throw new JsonException("Conflicting identities within one Garmin set.");
            if (key.Category == "INDOOR_BIKE") continue;
            result.Add(new(key.Category, key.ExerciseName, 1, true));
        }
        return result;
    }

    private static GarminExerciseKey ReadGarminKey(JsonElement element)
    {
        var category = RequireString(element, "category");
        string? name = null;
        if (element.TryGetProperty("name", out var nameField) &&
            nameField.ValueKind == JsonValueKind.String)
            name = nameField.GetString();
        return new(category, name);
    }

    private static JsonElement RequireArray(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Expected array property {field}.");
        return value;
    }

    private static string RequireString(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new JsonException($"Expected string property {field}.");
        return value.GetString()!;
    }

    private static bool TryLong(JsonElement value, out long number) =>
        value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out number) :
        long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
}
