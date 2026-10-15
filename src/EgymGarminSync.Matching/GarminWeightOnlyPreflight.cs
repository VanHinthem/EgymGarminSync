using System.Text.Json;
using System.Text.Json.Nodes;

namespace EgymGarminSync.Matching;

/// <summary>Pure offline JSON preflight. No HTTP, persistence or Garmin update support.</summary>
public static class GarminWeightOnlyPreflight
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

    public static WeightPreflightResult Build(string originalJson, IReadOnlyCollection<EgymExerciseWeight> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalJson);
        ArgumentNullException.ThrowIfNull(targets);
        using var document = JsonDocument.Parse(originalJson);
        var original = document.RootElement;
        if (!original.TryGetProperty("exerciseSets", out var rawSets) || rawSets.ValueKind != JsonValueKind.Array ||
            rawSets.GetArrayLength() != 36)
            throw new InvalidOperationException("Expected exactly 36 original Garmin set records.");
        if (targets.Count != 8 || targets.Select(t => t.EgymActivityId).Distinct().Count() != 8 ||
            targets.Any(t => !Keys.ContainsKey(t.EgymActivityId) || t.WeightKg <= 0))
            throw new InvalidOperationException("Expected eight distinct positive EGYM weights.");

        var proposal = JsonNode.Parse(originalJson)!.AsObject();
        var proposedSets = proposal["exerciseSets"]!.AsArray();
        var changes = new List<SetWeightChange>();
        var counts = Keys.Keys.ToDictionary(key => key, _ => 0);
        for (var i = 0; i < rawSets.GetArrayLength(); i++)
        {
            var record = rawSets[i];
            if (!record.TryGetProperty("setType", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Missing Garmin setType.");
            if (type.GetString() != "ACTIVE") continue;
            if (!record.TryGetProperty("exercises", out var exercises) || exercises.ValueKind != JsonValueKind.Array ||
                exercises.GetArrayLength() == 0)
                throw new InvalidOperationException("ACTIVE set has no exercise identity.");
            var first = exercises[0];
            var category = first.GetProperty("category").GetString();
            var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (category == "INDOOR_BIKE") continue;
            var match = Keys.Where(k => k.Value.Category == category && k.Value.ExerciseName == name).ToArray();
            if (match.Length != 1) throw new InvalidOperationException("Unknown or ambiguous strength identity.");
            var machineId = match[0].Key;
            counts[machineId]++;
            if (!record.TryGetProperty("weight", out var weight) || weight.ValueKind != JsonValueKind.Number ||
                !weight.TryGetDecimal(out var existing))
                throw new InvalidOperationException("Missing or nonnumeric original Garmin weight.");
            var requested = targets.Single(t => t.EgymActivityId == machineId).WeightKg * 1000m;
            if (requested == existing) continue;
            proposedSets[i]!["weight"] = requested;
            changes.Add(new(i, machineId, existing, requested));
        }
        if (counts.Any(x => x.Value != 2) || changes.Count != 14 ||
            changes.Select(c => c.EgymActivityId).Distinct().Count() != 7)
            throw new InvalidOperationException("Aug31 exercise coverage or 14-change assumption failed.");

        var proposedJson = proposal.ToJsonString();
        ValidateOnlyApprovedWeightsChanged(originalJson, proposedJson, changes);
        return new(proposedJson, changes);
    }

    public static void ValidateOnlyApprovedWeightsChanged(string originalJson, string proposedJson,
        IReadOnlyCollection<SetWeightChange> allowed)
    {
        using var oldDoc = JsonDocument.Parse(originalJson);
        using var newDoc = JsonDocument.Parse(proposedJson);
        var oldRoot = oldDoc.RootElement;
        var newRoot = newDoc.RootElement;
        var oldSets = oldRoot.GetProperty("exerciseSets");
        var newSets = newRoot.GetProperty("exerciseSets");
        if (oldSets.GetArrayLength() != newSets.GetArrayLength() ||
            allowed.Count != allowed.Select(x => x.SetIndex).Distinct().Count())
            throw new InvalidOperationException("Set count or allowlist identity changed.");
        for (var i = 0; i < oldSets.GetArrayLength(); i++)
        {
            var oldSet = oldSets[i];
            var newSet = newSets[i];
            var change = allowed.SingleOrDefault(x => x.SetIndex == i);
            if (change is null)
            {
                if (!JsonElement.DeepEquals(oldSet, newSet))
                    throw new InvalidOperationException($"Unexpected change in set {i}.");
                continue;
            }
            if (oldSet.GetProperty("weight").GetDecimal() != change.OldWeightGrams ||
                newSet.GetProperty("weight").GetDecimal() != change.NewWeightGrams)
                throw new InvalidOperationException("Allowed weight change is inconsistent.");
            var oldClone = JsonNode.Parse(oldSet.GetRawText())!.AsObject();
            var newClone = JsonNode.Parse(newSet.GetRawText())!.AsObject();
            oldClone.Remove("weight");
            newClone.Remove("weight");
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(oldClone),
                    JsonSerializer.SerializeToElement(newClone)))
                throw new InvalidOperationException($"Other fields changed in strength set {i}.");
        }
        var oldOther = JsonNode.Parse(originalJson)!.AsObject();
        var newOther = JsonNode.Parse(proposedJson)!.AsObject();
        oldOther.Remove("exerciseSets");
        newOther.Remove("exerciseSets");
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(oldOther),
                JsonSerializer.SerializeToElement(newOther)))
            throw new InvalidOperationException("Non-set payload fields changed.");
    }
}
public sealed record SetWeightChange(int SetIndex, long EgymActivityId, decimal OldWeightGrams, decimal NewWeightGrams);
public sealed record WeightPreflightResult(string ProposedJson, IReadOnlyList<SetWeightChange> Changes);
