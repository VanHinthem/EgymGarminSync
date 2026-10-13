using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class StrengthWeightMatcherTests
{
    [Fact]
    public void DifferentWeightsAreReportedInKilogramsRegardlessOfSetOrder()
    {
        var result = StrengthWeightMatcher.Compare(
            [new EgymExerciseWeight(333, 21m)],
            [
                new GarminRecordedSet("ROW", "SEATED_CABLE_ROW", 31000m, true),
                new GarminRecordedSet("BENCH_PRESS", null, 22000m, true),
                new GarminRecordedSet("BENCH_PRESS", null, 22000m, true)
            ]);
        var diff = Assert.Single(result);
        Assert.Equal(MatchStatus.Different, diff.Status);
        Assert.Equal(22m, diff.GarminKg);
        Assert.Equal(-1m, diff.DeltaKg);
    }

    [Fact]
    public void SameWeightsAcrossBothRoundsAreEqual()
    {
        var result = StrengthWeightMatcher.Compare(
            [new EgymExerciseWeight(335, 33m)],
            [
                new GarminRecordedSet("PULL_UP", "LAT_PULLDOWN", 33000m, true),
                new GarminRecordedSet("PULL_UP", "LAT_PULLDOWN", 33000m, true)
            ]);
        Assert.Equal(MatchStatus.Equal, Assert.Single(result).Status);
    }

    [Fact]
    public void ConflictingCircuitRoundWeightsRequireReview()
    {
        var result = StrengthWeightMatcher.Compare(
            [new EgymExerciseWeight(335, 33m)],
            [
                new GarminRecordedSet("PULL_UP", "LAT_PULLDOWN", 33000m, true),
                new GarminRecordedSet("PULL_UP", "LAT_PULLDOWN", 34000m, true)
            ]);
        Assert.Equal(MatchStatus.AmbiguousGarminWeight, Assert.Single(result).Status);
    }

    [Fact]
    public void NonStrengthGarminEntriesAreExcluded()
    {
        var result = StrengthWeightMatcher.Compare(
            [new EgymExerciseWeight(333, 21m)],
            [new GarminRecordedSet("BENCH_PRESS", null, 22000m, false)]);
        Assert.Equal(MatchStatus.MissingGarminSet, Assert.Single(result).Status);
    }

    [Fact]
    public void UnmappedEquipmentIsNotMatchedByPosition()
    {
        var result = StrengthWeightMatcher.Compare(
            [new EgymExerciseWeight(999, 10m)],
            [new GarminRecordedSet("BENCH_PRESS", null, 22000m, true)]);
        Assert.Equal(MatchStatus.UnmappedExercise, Assert.Single(result).Status);
    }
}
