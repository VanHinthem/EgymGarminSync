using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminExerciseSetsWriteContractTests
{
    private const long Id = 24180037460;
    private static string ValidPayload()
    {
        var sets = new JsonArray();
        for (var i = 0; i < 36; i++)
            sets.Add(new JsonObject
            {
                ["activityId"] = Id,
                ["setType"] = i % 2 == 0 ? "ACTIVE" : "REST",
                ["startTime"] = "2026-08-31T05:34:53Z",
                ["exercises"] = new JsonArray(new JsonObject
                {
                    ["activityId"] = Id, ["category"] = "BENCH_PRESS"
                })
            });
        return new JsonObject { ["activityId"] = Id, ["exerciseSets"] = sets }.ToJsonString();
    }

    [Fact]
    public void AcceptsFullRequiredShape() =>
        GarminExerciseSetsWriteContract.Validate(ValidPayload(), Id);

    [Fact]
    public void RejectsMissingNestedActivityId()
    {
        var root = JsonNode.Parse(ValidPayload())!;
        root["exerciseSets"]![0]!["exercises"]![0]!.AsObject().Remove("activityId");
        Assert.Throws<InvalidOperationException>(() =>
            GarminExerciseSetsWriteContract.Validate(root.ToJsonString(), Id));
    }

    [Fact]
    public void RejectsMissingStartTime()
    {
        var root = JsonNode.Parse(ValidPayload())!;
        root["exerciseSets"]![0]!.AsObject().Remove("startTime");
        Assert.Throws<InvalidOperationException>(() =>
            GarminExerciseSetsWriteContract.Validate(root.ToJsonString(), Id));
    }

    [Fact]
    public void RejectsWrongActivityId()
    {
        var root = JsonNode.Parse(ValidPayload())!;
        root["exerciseSets"]![0]!["activityId"] = 123;
        Assert.Throws<InvalidOperationException>(() =>
            GarminExerciseSetsWriteContract.Validate(root.ToJsonString(), Id));
    }
}
