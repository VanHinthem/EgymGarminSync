using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class RepetitionMatcherTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(15)]
    public void TwoSetsOfExpectedRepsMatch(int reps)
    {
        var result = RepetitionMatcher.Compare(
            [new EgymExerciseReps(333, [reps, reps])],
            [new GarminExerciseReps("BENCH_PRESS", null, reps, true),
             new GarminExerciseReps("BENCH_PRESS", null, reps, true)]);
        Assert.Equal(RepetitionStatus.Equal, Assert.Single(result).Status);
    }

    [Fact]
    public void DifferentRepsAreReported()
    {
        var result = RepetitionMatcher.Compare(
            [new EgymExerciseReps(333, [20, 20])],
            [new GarminExerciseReps("BENCH_PRESS", null, 20, true),
             new GarminExerciseReps("BENCH_PRESS", null, 19, true)]);
        Assert.Equal(RepetitionStatus.Different, Assert.Single(result).Status);
    }

    [Fact]
    public void MixedRepsAreComparedWithoutAssumingCircuitOrdering()
    {
        var result = RepetitionMatcher.Compare(
            [new EgymExerciseReps(333, [15, 14])],
            [new GarminExerciseReps("BENCH_PRESS", null, 14, true),
             new GarminExerciseReps("BENCH_PRESS", null, 15, true)]);
        Assert.Equal(RepetitionStatus.Equal, Assert.Single(result).Status);
    }

    [Fact]
    public void MissingSetIsFlagged()
    {
        var result = RepetitionMatcher.Compare(
            [new EgymExerciseReps(333, [15, 15])],
            [new GarminExerciseReps("BENCH_PRESS", null, 15, true)]);
        Assert.Equal(RepetitionStatus.MissingOrInvalidSets, Assert.Single(result).Status);
    }

    [Fact]
    public void BikeRecordsNeverCountAsStrength()
    {
        var result = RepetitionMatcher.Compare(
            [new EgymExerciseReps(333, [20, 20])],
            [new GarminExerciseReps("INDOOR_BIKE", null, null, false),
             new GarminExerciseReps("BENCH_PRESS", null, 20, true),
             new GarminExerciseReps("BENCH_PRESS", null, 20, true)]);
        Assert.Equal(RepetitionStatus.Equal, Assert.Single(result).Status);
    }
}
