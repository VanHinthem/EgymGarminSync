using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminOneSetReadbackTests
{
    private static string Snapshot()
    {
        var categories = new[]
        {
            ("INDOOR_BIKE", (string?)null), ("REST", null),
            ("BENCH_PRESS", null), ("REST", null),
            ("ROW", "SEATED_CABLE_ROW"), ("REST", null),
            ("SQUAT", "LEG_PRESS"), ("REST", null),
            ("CRUNCH", "WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER"), ("REST", null),
            ("PULL_UP", "LAT_PULLDOWN"), ("REST", null),
            ("CRUNCH", "WEIGHTED_LEG_EXTENSIONS"), ("REST", null),
            ("HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION"),
            ("LEG_CURL", "WEIGHTED_LEG_CURL")
        };
        var sets = new JsonArray();
        for (var round = 0; round < 2; round++)
            foreach (var (category, name) in categories)
                sets.Add(new JsonObject
                {
                    ["setType"] = category == "REST" ? "REST" : "ACTIVE",
                    ["weight"] = category == "BENCH_PRESS" ? 22000 : 30000,
                    ["startTime"] = "2026-08-31T05:34:53Z",
                    ["exercises"] = new JsonArray(new JsonObject { ["category"] = category, ["name"] = name }),
                    ["repetitionCount"] = 15,
                    ["duration"] = 30
                });
        for (var n = 0; n < 4; n++)
            sets.Add(new JsonObject { ["setType"] = "REST", ["duration"] = 30 });
        return new JsonObject { ["activityId"] = 24180037460L, ["exerciseSets"] = sets }.ToJsonString();
    }

    [Fact]
    public void RecognizesUntouchedAndSingleWeight()
    {
        var original = Snapshot();
        Assert.Equal(GarminTrialState.Original, GarminOneSetReadback.Classify(original, original));
        var changed = OneSetTrialPlanner.Prepare(original, original).ProposedJson;
        Assert.Equal(GarminTrialState.ChangedOneWeight, GarminOneSetReadback.Classify(original, changed));
    }

    [Fact]
    public void RejectsUnexpectedRestOrRepetitionChange()
    {
        var original = Snapshot();
        var modified = JsonNode.Parse(OneSetTrialPlanner.Prepare(original, original).ProposedJson)!;
        modified["exerciseSets"]![2]!["repetitionCount"] = 12;
        Assert.Equal(GarminTrialState.Unexpected, GarminOneSetReadback.Classify(original, modified.ToJsonString()));
        modified = JsonNode.Parse(original)!;
        modified["exerciseSets"]![1]!["duration"] = 99;
        Assert.Equal(GarminTrialState.Unexpected, GarminOneSetReadback.Classify(original, modified.ToJsonString()));
    }
}
