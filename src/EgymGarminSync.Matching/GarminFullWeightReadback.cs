using System.Text.Json;
using System.Text.Json.Nodes;

namespace EgymGarminSync.Matching;

/// <summary>Strict server readback comparison, tolerating only request-only activity IDs.</summary>
public static class GarminFullWeightReadback
{
    public static bool Matches(string expectedJson, string actualJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        if (JsonElement.DeepEquals(expected.RootElement, actual.RootElement)) return true;
        var expectedRoot = expected.RootElement;
        var actualRoot = JsonNode.Parse(actualJson)?.AsObject();
        if (actualRoot is null || !expectedRoot.TryGetProperty("exerciseSets", out var expectedSets) ||
            actualRoot["exerciseSets"] is not JsonArray actualSets ||
            actualSets.Count != expectedSets.GetArrayLength()) return false;
        for (var i = 0; i < actualSets.Count; i++)
        {
            var record = actualSets[i]?.AsObject();
            if (record is null) return false;
            if (!expectedSets[i].TryGetProperty("activityId", out _)) record.Remove("activityId");
            if (record["exercises"] is not JsonArray actualExercises ||
                !expectedSets[i].TryGetProperty("exercises", out var originalExercises) ||
                originalExercises.ValueKind != JsonValueKind.Array) continue;
            if (actualExercises.Count != originalExercises.GetArrayLength()) return false;
            for (var j = 0; j < actualExercises.Count; j++)
                if (!originalExercises[j].TryGetProperty("activityId", out _))
                    actualExercises[j]?.AsObject().Remove("activityId");
        }
        using var comparison = JsonDocument.Parse(actualRoot.ToJsonString());
        return JsonElement.DeepEquals(expected.RootElement, comparison.RootElement);
    }
}
