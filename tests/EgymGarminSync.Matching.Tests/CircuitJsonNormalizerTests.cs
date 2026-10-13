using System.Text.Json;
using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class CircuitJsonNormalizerTests
{
    [Fact]
    public void EgymBikeIsExcludedAndSetArrayCountsAsStrength()
    {
        using var json = JsonDocument.Parse("""
        {"exercises":[{"activity":{"activityId":31},"sets":[{},{}]},
                      {"activity":{"activityId":333},"sets":[{"numberOfReps":15},{"numberOfReps":15}]},
                      {"activity":{"activityId":334},"sets":[{},{}]}]}
        """);
        var counts = CircuitJsonNormalizer.EgymCounts(json.RootElement);
        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts.Single(x => x.EgymActivityId == 333).SetCount);
    }

    [Fact]
    public void GarminRepeatingExerciseNamesCountOnlyOncePerActiveRecord()
    {
        using var json = JsonDocument.Parse("""
        {"exerciseSets":[
          {"setType":"ACTIVE","exercises":[{"category":"INDOOR_BIKE","name":null}]},
          {"setType":"REST"},
          {"setType":"ACTIVE","exercises":[{"category":"BENCH_PRESS","name":null},{"category":"BENCH_PRESS","name":null},{"category":"BENCH_PRESS","name":null}]},
          {"setType":"ACTIVE","exercises":[{"category":"BENCH_PRESS","name":null}]}
        ]}
        """);
        var counts = CircuitJsonNormalizer.GarminCounts(json.RootElement);
        Assert.Equal(2, counts.Count);
        Assert.All(counts, x => Assert.Equal(1, x.SetCount));
    }

    [Fact]
    public void UnknownActiveGarminExerciseIsNotSilentlyIgnored()
    {
        using var json = JsonDocument.Parse("""
        {"exerciseSets":[{"setType":"ACTIVE","exercises":[{"category":"CURL","name":"UNKNOWN"}]}]}
        """);
        var counts = CircuitJsonNormalizer.GarminCounts(json.RootElement);
        Assert.Equal("CURL", Assert.Single(counts).Category);
    }

    [Fact]
    public void GarminMixedNamesInsideOneSetFailClosed()
    {
        using var json = JsonDocument.Parse("""
        {"exerciseSets":[{"setType":"ACTIVE","exercises":[{"category":"BENCH_PRESS","name":null},{"category":"ROW","name":"SEATED_CABLE_ROW"}]}]}
        """);
        Assert.Throws<JsonException>(() => CircuitJsonNormalizer.GarminCounts(json.RootElement));
    }

    [Fact]
    public void MissingStrengthSetArrayFailsClosed()
    {
        using var json = JsonDocument.Parse("""{"exercises":[{"activity":{"activityId":333}}]}""");
        Assert.Throws<JsonException>(() => CircuitJsonNormalizer.EgymCounts(json.RootElement));
    }
}
