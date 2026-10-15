using System.Text.Json;
using System.Text.Json.Nodes;

namespace EgymGarminSync.Matching;

/// <summary>Constructs an offline trial request document; never changes the original backup.</summary>
public static class GarminTrialPayloadNormalizer
{
    public static string Normalize(string proposedJson, long activityId)
    {
        if (activityId <= 0) throw new ArgumentOutOfRangeException(nameof(activityId));
        var root = JsonNode.Parse(proposedJson)?.AsObject()
            ?? throw new InvalidOperationException("Invalid payload.");
        if (root["activityId"]?.GetValue<long>() != activityId)
            throw new InvalidOperationException("Root activityId mismatch.");
        var sets = root["exerciseSets"]?.AsArray()
            ?? throw new InvalidOperationException("Missing exerciseSets.");
        if (sets.Count != 36) throw new InvalidOperationException("Expected 36 sets.");
        foreach (var item in sets)
        {
            var set = item?.AsObject() ?? throw new InvalidOperationException("Invalid set.");
            AddId(set, activityId);
            if (set["exercises"] is not JsonArray exercises) continue;
            foreach (var exercise in exercises)
                AddId(exercise?.AsObject() ?? throw new InvalidOperationException("Invalid exercise."), activityId);
        }
        var normalized = root.ToJsonString();
        GarminExerciseSetsWriteContract.Validate(normalized, activityId);
        // Strip only the inserted IDs and verify the entire result equals the candidate.
        var stripped = JsonNode.Parse(normalized)!.AsObject();
        foreach (var item in stripped["exerciseSets"]!.AsArray())
        {
            var set = item!.AsObject();
            set.Remove("activityId");
            if (set["exercises"] is not JsonArray exercises) continue;
            foreach (var exercise in exercises)
                exercise!.AsObject().Remove("activityId");
        }
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(stripped),
                JsonSerializer.SerializeToElement(JsonNode.Parse(proposedJson))))
            throw new InvalidOperationException("Unexpected update payload mutation.");
        return normalized;
    }

    private static void AddId(JsonObject obj, long activityId)
    {
        if (obj.TryGetPropertyValue("activityId", out var value))
        {
            if (value is null || value.GetValue<long>() != activityId)
                throw new InvalidOperationException("Existing activityId differs; refusing overwrite.");
        }
        else obj["activityId"] = activityId;
    }
}
