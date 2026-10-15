using System.Text.Json;
using System.Text.Json.Nodes;

namespace EgymGarminSync.Matching;

/// <summary>Pure comparison of server readback against original and single-set trial.</summary>
public static class GarminOneSetReadback
{
    public static GarminTrialState Classify(string backupJson, string liveJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveJson);
        using var original = JsonDocument.Parse(backupJson);
        using var live = JsonDocument.Parse(liveJson);
        if (JsonElement.DeepEquals(original.RootElement, live.RootElement))
            return GarminTrialState.Original;
        var expected = OneSetTrialPlanner.Prepare(backupJson, backupJson).ProposedJson;
        using var candidate = JsonDocument.Parse(expected);
        if (JsonElement.DeepEquals(candidate.RootElement, live.RootElement))
            return GarminTrialState.ChangedOneWeight;
        // Garmin may echo request-only activityId fields. Remove ONLY those fields where
        // they were not present in the original; reject all other server-side changes.
        var normalized = JsonNode.Parse(liveJson)!.AsObject();
        var sets = normalized["exerciseSets"]?.AsArray();
        if (sets is null || sets.Count != 36) return GarminTrialState.Unexpected;
        foreach (var item in sets)
        {
            var set = item?.AsObject();
            if (set is null) return GarminTrialState.Unexpected;
            set.Remove("activityId");
            if (set["exercises"] is not JsonArray exercises) continue;
            foreach (var exercise in exercises)
                exercise?.AsObject().Remove("activityId");
        }
        using var trimmed = JsonDocument.Parse(normalized.ToJsonString());
        return JsonElement.DeepEquals(candidate.RootElement, trimmed.RootElement)
            ? GarminTrialState.ChangedOneWeight
            : JsonElement.DeepEquals(original.RootElement, trimmed.RootElement)
                ? GarminTrialState.Original : GarminTrialState.Unexpected;
    }
}
public enum GarminTrialState { Original, ChangedOneWeight, Unexpected }
