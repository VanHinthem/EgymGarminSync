using System.Text.Json;
using System.Text.Json.Nodes;

namespace EgymGarminSync.Matching;

/// <summary>Pure JSON preparation for one Aug31 Chest Press trial. No transport and no writes.</summary>
public static class OneSetTrialPlanner
{
    public const int TargetIndex = 2;
    public const decimal OldGrams = 22000m;
    public const decimal TrialGrams = 21000m;

    public static WeightPreflightResult Prepare(string backupJson, string latestJson)
    {
        using var saved = JsonDocument.Parse(backupJson);
        using var latest = JsonDocument.Parse(latestJson);
        if (!JsonElement.DeepEquals(saved.RootElement, latest.RootElement))
            throw new InvalidOperationException("Live Garmin set data differs from the verified snapshot; trial blocked.");

        var root = saved.RootElement;
        if (!root.TryGetProperty("exerciseSets", out var sets) || sets.ValueKind != JsonValueKind.Array ||
            sets.GetArrayLength() != 36)
            throw new InvalidOperationException("Expected 36 Garmin set records.");
        var chestPressSets = new List<int>();
        for (var i = 0; i < sets.GetArrayLength(); i++)
        {
            var item = sets[i];
            if (!item.TryGetProperty("setType", out var st) || st.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Set type missing.");
            if (st.GetString() != "ACTIVE") continue;
            if (!item.TryGetProperty("exercises", out var exercises) || exercises.ValueKind != JsonValueKind.Array ||
                exercises.GetArrayLength() == 0) throw new InvalidOperationException("ACTIVE identity missing.");
            var first = exercises[0];
            if (first.GetProperty("category").GetString() != "BENCH_PRESS") continue;
            var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name is not null) throw new InvalidOperationException("Unexpected Chest Press name.");
            if (!item.TryGetProperty("weight", out var weight) || weight.ValueKind != JsonValueKind.Number ||
                weight.GetDecimal() != OldGrams)
                throw new InvalidOperationException("Unexpected original Chest Press weight.");
            chestPressSets.Add(i);
        }
        if (chestPressSets.Count != 2 || chestPressSets[0] != TargetIndex || chestPressSets[1] != 18)
            throw new InvalidOperationException("Chest Press set indices changed.");
        var proposed = JsonNode.Parse(backupJson)!.AsObject();
        proposed["exerciseSets"]![TargetIndex]!["weight"] = TrialGrams;
        var proposalJson = proposed.ToJsonString();
        SetWeightChange[] onlyChange = [new(TargetIndex, 333, OldGrams, TrialGrams)];
        GarminWeightOnlyPreflight.ValidateOnlyApprovedWeightsChanged(backupJson, proposalJson, onlyChange);
        return new(proposalJson, onlyChange);
    }
}
