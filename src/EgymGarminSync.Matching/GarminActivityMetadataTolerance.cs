using System.Text.Json;

namespace EgymGarminSync.Matching;

/// <summary>Allows precisely Garmin's observed addition of totalSets=36, no other summary changes.</summary>
public static class GarminActivityMetadataTolerance
{
    public static bool IsAcceptable(string originalJson, string liveJson)
    {
        using var oldDoc = JsonDocument.Parse(originalJson);
        using var newDoc = JsonDocument.Parse(liveJson);
        if (JsonElement.DeepEquals(oldDoc.RootElement, newDoc.RootElement)) return true;
        var oldRoot = oldDoc.RootElement;
        var newRoot = newDoc.RootElement;
        if (!oldRoot.TryGetProperty("summaryDTO", out var oldSummary) ||
            !newRoot.TryGetProperty("summaryDTO", out var newSummary) ||
            oldSummary.ValueKind != JsonValueKind.Object || newSummary.ValueKind != JsonValueKind.Object ||
            oldSummary.TryGetProperty("totalSets", out _) ||
            !newSummary.TryGetProperty("totalSets", out var total) ||
            total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count) || count != 36)
            return false;
        var copy = System.Text.Json.Nodes.JsonNode.Parse(liveJson)!.AsObject();
        copy["summaryDTO"]!.AsObject().Remove("totalSets");
        return JsonElement.DeepEquals(oldRoot, JsonSerializer.SerializeToElement(copy));
    }
}
