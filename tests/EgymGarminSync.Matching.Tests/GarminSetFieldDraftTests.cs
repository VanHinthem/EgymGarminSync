using System.Text.Json;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class GarminSetFieldDraftTests
{
    private const string Original = """
      {"activityId":123,"exerciseSets":[
        {"setType":"ACTIVE","weight":22000,"repetitionCount":12,"duration":40,
         "exercises":[{"category":"BENCH_PRESS"}]},
        {"setType":"REST","duration":20},
        {"setType":"ACTIVE","weight":31000,"repetitionCount":15,
         "exercises":[{"category":"ROW"}]}
      ],"other":"unchanged"}
      """;

    [Fact]
    public void UpdatesOnlySelectedWeightAndRepetitions()
    {
        var updated = GarminSetFieldDraft.Apply(Original,
        [
            new(0, "weight", 22000, 21000),
            new(0, "repetitionCount", 12, 15)
        ]);
        using var before = JsonDocument.Parse(Original);
        using var after = JsonDocument.Parse(updated);
        Assert.Equal(21000m, after.RootElement.GetProperty("exerciseSets")[0].GetProperty("weight").GetDecimal());
        Assert.Equal(15, after.RootElement.GetProperty("exerciseSets")[0].GetProperty("repetitionCount").GetInt32());
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("exerciseSets")[1],
            after.RootElement.GetProperty("exerciseSets")[1]));
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("exerciseSets")[2],
            after.RootElement.GetProperty("exerciseSets")[2]));
        Assert.Equal("unchanged",after.RootElement.GetProperty("other").GetString());
    }

    [Fact]
    public void RefusesStaleDuplicateAndRestWrites()
    {
        Assert.Throws<InvalidOperationException>(()=>GarminSetFieldDraft.Apply(Original,
            [new(0,"repetitionCount",15,16)]));
        Assert.Throws<InvalidOperationException>(()=>GarminSetFieldDraft.Apply(Original,
            [new(0,"weight",22000,21000),new(0,"weight",22000,22000)]));
        Assert.Throws<InvalidOperationException>(()=>GarminSetFieldDraft.Apply(Original,
            [new(1,"weight",0,10)]));
        Assert.Throws<InvalidOperationException>(()=>GarminSetFieldDraft.Apply(Original,
            [new(0,"duration",40,50)]));
    }

    [Fact]
    public void RefusesFractionalRepetitionsAndNegativeWeights()
    {
        Assert.Throws<InvalidOperationException>(()=>GarminSetFieldDraft.Apply(Original,
            [new(0,"repetitionCount",12,15.5m)]));
        Assert.Throws<InvalidOperationException>(()=>GarminSetFieldDraft.Apply(Original,
            [new(0,"weight",22000,-1000)]));
    }
}
