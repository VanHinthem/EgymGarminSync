using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminFullWeightReadbackTests
{
    private static string Original()
    {
        var sets = new JsonArray();
        for (var i = 0; i < 36; i++)
            sets.Add(new JsonObject
            {
                ["setType"] = i % 2 == 0 ? "ACTIVE" : "REST",
                ["weight"] = 22000,
                ["exercises"] = new JsonArray(new JsonObject { ["category"] = "BENCH_PRESS" }),
                ["repetitionCount"] = 15,
                ["duration"] = 30
            });
        return new JsonObject { ["activityId"] = 24180037460L, ["exerciseSets"] = sets }.ToJsonString();
    }

    [Fact]
    public void MatchesIdenticalAndServerAddedRequestIds()
    {
        var before = Original();
        Assert.True(GarminFullWeightReadback.Matches(before, before));
        var server = JsonNode.Parse(before)!;
        foreach (var set in server["exerciseSets"]!.AsArray())
        {
            set!["activityId"] = 24180037460L;
            set["exercises"]![0]!["activityId"] = 24180037460L;
        }
        Assert.True(GarminFullWeightReadback.Matches(before, server.ToJsonString()));
    }

    [Fact]
    public void RejectsChangedSetAndUnrelatedSummary()
    {
        var before = Original();
        var live = JsonNode.Parse(before)!;
        live["exerciseSets"]![2]!["weight"] = 21000;
        Assert.False(GarminFullWeightReadback.Matches(before, live.ToJsonString()));
        live = JsonNode.Parse(before)!;
        live["extra"] = 1;
        Assert.False(GarminFullWeightReadback.Matches(before, live.ToJsonString()));
    }
}
