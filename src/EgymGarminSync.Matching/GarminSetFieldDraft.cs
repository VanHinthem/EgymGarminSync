using System.Text.Json;
using System.Text.Json.Nodes;

namespace EgymGarminSync.Matching;

/// <summary>Pure field-specific preview. Does not permit executing a Garmin PUT.</summary>
public static class GarminSetFieldDraft
{
    public static string Apply(string originalJson, IReadOnlyCollection<SetFieldEdit> edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalJson);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new InvalidOperationException("No edits selected.");
        using var doc = JsonDocument.Parse(originalJson);
        var source = doc.RootElement.GetProperty("exerciseSets");
        var root = JsonNode.Parse(originalJson)!.AsObject();
        var sets = root["exerciseSets"]!.AsArray();
        if (sets.Count != source.GetArrayLength()) throw new InvalidOperationException("Set count mismatch.");
        var unique = new HashSet<(int,string)>();
        foreach (var edit in edits)
        {
            if (edit.SetIndex < 0 || edit.SetIndex >= sets.Count ||
                edit.Field is not ("weight" or "repetitionCount") ||
                !unique.Add((edit.SetIndex, edit.Field)))
                throw new InvalidOperationException("Invalid or duplicate field edit.");
            var original = source[edit.SetIndex];
            if (original.GetProperty("setType").GetString() != "ACTIVE" ||
                !original.TryGetProperty(edit.Field, out var existing) ||
                existing.ValueKind != JsonValueKind.Number ||
                existing.GetDecimal() != edit.Expected)
                throw new InvalidOperationException("Set field no longer matches expected original value.");
            if (edit.Value < 0 || (edit.Field == "repetitionCount" &&
                (edit.Value != decimal.Truncate(edit.Value) || edit.Value > 1000)) ||
                (edit.Field == "weight" && edit.Value > 1000000))
                throw new InvalidOperationException("Unsupported proposed field value.");
            sets[edit.SetIndex]![edit.Field] = edit.Value;
        }
        var proposed = root.ToJsonString();
        var verified = JsonNode.Parse(proposed)!.AsObject();
        foreach (var edit in edits) verified["exerciseSets"]![edit.SetIndex]![edit.Field] = edit.Expected;
        using var reverted = JsonDocument.Parse(verified.ToJsonString());
        if (!JsonElement.DeepEquals(doc.RootElement, reverted.RootElement))
            throw new InvalidOperationException("Unexpected collateral changes in proposed payload.");
        return proposed;
    }
}
public sealed record SetFieldEdit(int SetIndex, string Field, decimal Expected, decimal Value);
