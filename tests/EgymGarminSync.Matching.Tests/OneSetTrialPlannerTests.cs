using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class OneSetTrialPlannerTests
{
    private static string Fixture()
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
            ("HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION"), ("REST", null),
            ("LEG_CURL", "WEIGHTED_LEG_CURL"), ("REST", null)
        };
        var all = new JsonArray();
        for (var j = 0; j < 2; j++)
            foreach (var (category, name) in categories)
                all.Add(new JsonObject {
                    ["setType"] = category == "REST" ? "REST" : "ACTIVE",
                    ["weight"] = category == "BENCH_PRESS" ? 22000 : 30000,
                    ["repetitionCount"] = 15,
                    ["exercises"] = new JsonArray(new JsonObject { ["category"] = category, ["name"] = name }),
                    ["other"] = "must survive"
                });
        return new JsonObject { ["exerciseSets"] = all, ["keep"] = true }.ToJsonString();
    }

    [Fact]
    public void OnlyIndexTwoChanges()
    {
        var json = Fixture();
        var proposal = OneSetTrialPlanner.Prepare(json, json);
        Assert.Single(proposal.Changes);
        Assert.Equal(2, proposal.Changes[0].SetIndex);
        GarminWeightOnlyPreflight.ValidateOnlyApprovedWeightsChanged(json, proposal.ProposedJson, proposal.Changes);
        var result = JsonNode.Parse(proposal.ProposedJson)!;
        Assert.Equal(21000, result["exerciseSets"]![2]!["weight"]!.GetValue<decimal>());
        Assert.Equal(22000, result["exerciseSets"]![20]!["weight"]!.GetValue<decimal>());
    }

    [Fact]
    public void RefusesChangedLiveData()
    {
        var json = Fixture();
        var latest = JsonNode.Parse(json)!;
        latest["exerciseSets"]![4]!["repetitionCount"] = 99;
        Assert.Throws<InvalidOperationException>(() => OneSetTrialPlanner.Prepare(json, latest.ToJsonString()));
    }

    [Fact]
    public void RefusesUnexpectedOriginalWeight()
    {
        var json = JsonNode.Parse(Fixture())!;
        json["exerciseSets"]![2]!["weight"] = 23000;
        var value = json.ToJsonString();
        Assert.Throws<InvalidOperationException>(() => OneSetTrialPlanner.Prepare(value, value));
    }
}
