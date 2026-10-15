using System.Text.Json;
using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminOneSetRestorePlannerTests
{
    private const long Id = 24180037460;

    [Fact]
    public void PreparesOriginalPayloadOnlyAfterExpectedChange()
    {
        var original = GarminOneSetReadbackTests.Snapshot();
        var changed = OneSetTrialPlanner.Prepare(original, original).ProposedJson;
        var restored = GarminOneSetRestorePlanner.Prepare(original, changed, Id);
        GarminExerciseSetsWriteContract.Validate(restored, Id);
        using var doc = JsonDocument.Parse(restored);
        Assert.Equal(22000m, doc.RootElement.GetProperty("exerciseSets")[2].GetProperty("weight").GetDecimal());
        Assert.Equal(36, doc.RootElement.GetProperty("exerciseSets").GetArrayLength());
    }

    [Fact]
    public void RefusesOriginalStateAndCollateralModification()
    {
        var original = GarminOneSetReadbackTests.Snapshot();
        Assert.Throws<InvalidOperationException>(() => GarminOneSetRestorePlanner.Prepare(original, original, Id));
        var changed = JsonNode.Parse(OneSetTrialPlanner.Prepare(original, original).ProposedJson)!;
        changed["exerciseSets"]![1]!["duration"] = 999;
        Assert.Throws<InvalidOperationException>(() =>
            GarminOneSetRestorePlanner.Prepare(original, changed.ToJsonString(), Id));
    }

    [Fact]
    public void RefusesWrongActivityId()
    {
        var original = GarminOneSetReadbackTests.Snapshot();
        var changed = OneSetTrialPlanner.Prepare(original, original).ProposedJson;
        Assert.Throws<InvalidOperationException>(() => GarminOneSetRestorePlanner.Prepare(original, changed, 999));
    }
}
