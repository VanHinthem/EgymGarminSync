using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class StrengthSessionCompletenessTests
{
    private static readonly long[] MachineIds = [333, 334, 581, 330, 335, 329, 331, 332];

    private static readonly GarminExerciseSetCount[] FullGarmin =
    [
        new("BENCH_PRESS", null, 2, true),
        new("ROW", "SEATED_CABLE_ROW", 2, true),
        new("SQUAT", "LEG_PRESS", 2, true),
        new("CRUNCH", "WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER", 2, true),
        new("PULL_UP", "LAT_PULLDOWN", 2, true),
        new("CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 2, true),
        new("HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION", 2, true),
        new("LEG_CURL", "WEIGHTED_LEG_CURL", 2, true),
        new("INDOOR_BIKE", null, 2, false),
        new("REST", null, 18, false)
    ];

    private static EgymExerciseSetCount[] FullEgym() =>
        MachineIds.Select(id => new EgymExerciseSetCount(id, 2)).ToArray();

    [Fact]
    public void FullTwoRoundCircuitIgnoresBikeAndRest()
    {
        var result = StrengthSessionCompleteness.Check(FullEgym(), FullGarmin, MachineIds, 2);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void MissingGarminRoundIsIncomplete()
    {
        var partial = FullGarmin.Select(x => x.Category == "BENCH_PRESS" ? x with { SetCount = 1 } : x).ToArray();
        var result = StrengthSessionCompleteness.Check(FullEgym(), partial, MachineIds, 2);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Issues, x => x.Side == CompletenessSide.Garmin && x.EgymActivityId == 333 && x.ActualSets == 1);
    }

    [Fact]
    public void MissingEgymMachineIsIncomplete()
    {
        var result = StrengthSessionCompleteness.Check(FullEgym().Where(x => x.EgymActivityId != 581).ToArray(),
            FullGarmin, MachineIds, 2);
        Assert.Contains(result.Issues, x => x.Side == CompletenessSide.Egym && x.EgymActivityId == 581);
    }

    [Fact]
    public void UnknownExerciseMappingBlocksCompleteness()
    {
        var result = StrengthSessionCompleteness.Check([new(999, 2)], [], [999], 2);
        Assert.Contains(result.Issues, x => x.Side == CompletenessSide.Mapping && x.Reason == "UnknownExercise");
    }

    [Fact]
    public void UnexpectedStrengthMachineIsFlagged()
    {
        var extra = FullGarmin.Append(new GarminExerciseSetCount("CURL", "BICEPS_CURL", 1, true)).ToArray();
        var result = StrengthSessionCompleteness.Check(FullEgym(), extra, MachineIds, 2);
        Assert.Contains(result.Issues, x => x.Side == CompletenessSide.Garmin && x.Reason == "UnexpectedExercise");
    }

    [Fact]
    public void DuplicateEgymIdentityIsNotSilentlyCombined()
    {
        var result = StrengthSessionCompleteness.Check(FullEgym().Append(new EgymExerciseSetCount(333, 2)).ToArray(),
            FullGarmin, MachineIds, 2);
        Assert.Contains(result.Issues, x => x.Side == CompletenessSide.Egym && x.Reason == "MissingOrDuplicateExercise");
    }
}
