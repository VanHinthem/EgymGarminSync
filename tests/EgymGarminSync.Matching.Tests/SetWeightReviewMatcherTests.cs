using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class SetWeightReviewMatcherTests
{
    [Fact]
    public void PerSetWeightsMatchWithoutAssumingOrder()
    {
        var egym = new Dictionary<long, IReadOnlyList<decimal>> { [329] = [29m, 28m] };
        var garmin = new[]
        {
            new GarminRecordedSet("CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 28000m, true),
            new GarminRecordedSet("CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 29000m, true)
        };
        Assert.True(Assert.Single(SetWeightReviewMatcher.Compare(egym, garmin)).Equal);
    }

    [Fact]
    public void July8VariableWeightsRemainReviewableAsIndividualValues()
    {
        var egym = new Dictionary<long, IReadOnlyList<decimal>> { [329] = [29m, 28m], [335] = [39m, 40m] };
        var garmin = new[]
        {
            new GarminRecordedSet("CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 23000m, true),
            new GarminRecordedSet("CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 23000m, true),
            new GarminRecordedSet("PULL_UP", "LAT_PULLDOWN", 36000m, true),
            new GarminRecordedSet("PULL_UP", "LAT_PULLDOWN", 36000m, true)
        };
        var result = SetWeightReviewMatcher.Compare(egym, garmin);
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.False(item.Equal));
        Assert.Equal(new decimal[] { 29m, 28m }, result.Single(x => x.ExerciseId == 329).EgymKg);
    }

    [Fact]
    public void MissingGarminSetIsNotEqual()
    {
        var result = SetWeightReviewMatcher.Compare(
            new Dictionary<long, IReadOnlyList<decimal>> { [333] = [27m, 27m] },
            [new GarminRecordedSet("BENCH_PRESS", null, 27000m, true)]);
        Assert.False(Assert.Single(result).Equal);
    }
}
