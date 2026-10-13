using System.Text.Json;
using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminWeightOnlyPreflightTests
{
    private static readonly (long Id, string Category, string? Name, int Old, int Target)[] Equipment =
    [
        (333, "BENCH_PRESS", null, 22, 21),
        (334, "ROW", "SEATED_CABLE_ROW", 31, 32),
        (581, "SQUAT", "LEG_PRESS", 89, 95),
        (330, "CRUNCH", "WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER", 24, 30),
        (335, "PULL_UP", "LAT_PULLDOWN", 33, 33),
        (329, "CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 20, 22),
        (331, "HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION", 43, 44),
        (332, "LEG_CURL", "WEIGHTED_LEG_CURL", 22, 23)
    ];

    private static IReadOnlyCollection<EgymExerciseWeight> Targets() =>
        Equipment.Select(e => new EgymExerciseWeight(e.Id, e.Target)).ToArray();

    private static string Fixture()
    {
        var sets = new JsonArray();
        for (var round = 0; round < 2; round++)
            foreach (var e in Equipment)
                sets.Add(new JsonObject
                {
                    ["setType"] = "ACTIVE", ["weight"] = e.Old * 1000,
                    ["repetitionCount"] = 15, ["startTime"] = "2026-08-31T06:00:00Z",
                    ["exercises"] = new JsonArray(new JsonObject
                    { ["category"] = e.Category, ["name"] = e.Name, ["custom"] = "preserve" })
                });
        for (var i = 0; i < 2; i++)
            sets.Add(new JsonObject { ["setType"] = "ACTIVE", ["weight"] = null,
                ["exercises"] = new JsonArray(new JsonObject { ["category"] = "INDOOR_BIKE" }),
                ["duration"] = 533 });
        for (var i = 0; i < 18; i++)
            sets.Add(new JsonObject { ["setType"] = "REST", ["weight"] = null, ["duration"] = 30 });
        return new JsonObject { ["exerciseSets"] = sets, ["extra"] = "keep" }.ToJsonString();
    }

    [Fact]
    public void OnlyFourteenStrengthWeightsChange()
    {
        var original = Fixture();
        var result = GarminWeightOnlyPreflight.Build(original, Targets());
        Assert.Equal(14, result.Changes.Count);
        Assert.DoesNotContain(result.Changes, x => x.EgymActivityId == 335);
        GarminWeightOnlyPreflight.ValidateOnlyApprovedWeightsChanged(original, result.ProposedJson, result.Changes);
        using var doc = JsonDocument.Parse(result.ProposedJson);
        Assert.Equal(36, doc.RootElement.GetProperty("exerciseSets").GetArrayLength());
    }

    [Fact]
    public void RejectsUnapprovedRepetitionEdit()
    {
        var original = Fixture();
        var result = GarminWeightOnlyPreflight.Build(original, Targets());
        var node = JsonNode.Parse(result.ProposedJson)!;
        node["exerciseSets"]![0]!["repetitionCount"] = 20;
        Assert.Throws<InvalidOperationException>(() =>
            GarminWeightOnlyPreflight.ValidateOnlyApprovedWeightsChanged(original, node.ToJsonString(), result.Changes));
    }

    [Fact]
    public void RejectsUnapprovedBikeEdit()
    {
        var original = Fixture();
        var result = GarminWeightOnlyPreflight.Build(original, Targets());
        var node = JsonNode.Parse(result.ProposedJson)!;
        node["exerciseSets"]![16]!["duration"] = 999;
        Assert.Throws<InvalidOperationException>(() =>
            GarminWeightOnlyPreflight.ValidateOnlyApprovedWeightsChanged(original, node.ToJsonString(), result.Changes));
    }

    [Fact]
    public void RejectsMissingGarminSet()
    {
        var node = JsonNode.Parse(Fixture())!;
        node["exerciseSets"]!.AsArray().RemoveAt(0);
        Assert.Throws<InvalidOperationException>(() => GarminWeightOnlyPreflight.Build(node.ToJsonString(), Targets()));
    }

    [Fact]
    public void RejectsMissingOriginalWeight()
    {
        var node = JsonNode.Parse(Fixture())!;
        node["exerciseSets"]![0]!["weight"] = null;
        Assert.Throws<InvalidOperationException>(() => GarminWeightOnlyPreflight.Build(node.ToJsonString(), Targets()));
    }
}
