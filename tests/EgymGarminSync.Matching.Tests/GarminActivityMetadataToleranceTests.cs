using System.Text.Json.Nodes;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminActivityMetadataToleranceTests
{
    private const string Before = """{"activityId":24180037460,"summaryDTO":{"duration":4236.322,"calories":635},"name":"EGYM Negatief"}""";

    [Fact]
    public void AllowsExactlyServerAddedTotalSets36()
    {
        const string after = """{"activityId":24180037460,"summaryDTO":{"duration":4236.322,"calories":635,"totalSets":36},"name":"EGYM Negatief"}""";
        Assert.True(GarminActivityMetadataTolerance.IsAcceptable(Before, Before));
        Assert.True(GarminActivityMetadataTolerance.IsAcceptable(Before, after));
    }

    [Fact]
    public void RejectsDifferentSetCountOrChangedOtherMetadata()
    {
        var wrongCount = JsonNode.Parse(Before)!;
        wrongCount["summaryDTO"]!["totalSets"] = 35;
        Assert.False(GarminActivityMetadataTolerance.IsAcceptable(Before, wrongCount.ToJsonString()));
        var otherChange = JsonNode.Parse(Before)!;
        otherChange["summaryDTO"]!["totalSets"] = 36;
        otherChange["summaryDTO"]!["calories"] = 636;
        Assert.False(GarminActivityMetadataTolerance.IsAcceptable(Before, otherChange.ToJsonString()));
    }

    [Fact]
    public void RejectsOriginalAlreadyHavingTotalSetsAndValueChanged()
    {
        const string before = """{"summaryDTO":{"totalSets":35}}""";
        const string after = """{"summaryDTO":{"totalSets":36}}""";
        Assert.False(GarminActivityMetadataTolerance.IsAcceptable(before, after));
    }
}
