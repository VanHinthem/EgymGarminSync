using System.Text.Json;
using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminTrialPayloadNormalizerTests
{
    private const long Id = 24180037460;

    private static string Source()
    {
        var sets = new JsonArray();
        for (var i = 0; i < 36; i++)
        {
            var active = i % 2 == 0;
            sets.Add(new JsonObject
            {
                ["setType"] = active ? "ACTIVE" : "REST",
                ["startTime"] = "2026-08-31T05:34:53Z",
                ["weight"] = active ? 21000 : null,
                ["exercises"] = active
                    ? new JsonArray(new JsonObject { ["category"] = "BENCH_PRESS", ["extra"] = "keep" })
                    : new JsonArray(),
                ["other"] = "preserve"
            });
        }
        return new JsonObject { ["activityId"] = Id, ["exerciseSets"] = sets, ["metadata"] = "unchanged" }.ToJsonString();
    }

    [Fact]
    public void AddsIdsButDoesNotChangeExistingValues()
    {
        var source = Source();
        var normalized = GarminTrialPayloadNormalizer.Normalize(source, Id);
        var inventory = GarminExerciseSetsWriteContract.Inspect(normalized, Id);
        Assert.Equal(0, inventory.SetIdMissing);
        Assert.Equal(0, inventory.ExerciseIdMissing);
        Assert.Equal(0, inventory.StartTimeMissing);
        using var doc = JsonDocument.Parse(normalized);
        Assert.Equal("unchanged", doc.RootElement.GetProperty("metadata").GetString());
    }

    [Fact]
    public void RefusesExistingConflictingSetId()
    {
        var root = JsonNode.Parse(Source())!;
        root["exerciseSets"]![0]!["activityId"] = 123L;
        Assert.Throws<InvalidOperationException>(() =>
            GarminTrialPayloadNormalizer.Normalize(root.ToJsonString(), Id));
    }
}
