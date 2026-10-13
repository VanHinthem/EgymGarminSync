using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class ReadOnlyComparisonReportTests
{
    private static readonly long[] Ids = [333, 334, 581, 330, 335, 329, 331, 332];
    private static readonly (long Id, string Category, string? Name, decimal Egym, decimal Garmin)[] Entries =
    [
        (333, "BENCH_PRESS", null, 21m, 22m),
        (334, "ROW", "SEATED_CABLE_ROW", 32m, 31m),
        (581, "SQUAT", "LEG_PRESS", 95m, 89m),
        (330, "CRUNCH", "WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER", 30m, 24m),
        (335, "PULL_UP", "LAT_PULLDOWN", 33m, 33m),
        (329, "CRUNCH", "WEIGHTED_LEG_EXTENSIONS", 22m, 20m),
        (331, "HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION", 44m, 43m),
        (332, "LEG_CURL", "WEIGHTED_LEG_CURL", 23m, 22m)
    ];

    private static ComparisonReport Build(SessionMatch session, bool omitGarminRow = false) =>
        ReadOnlyComparisonReport.Build(session,
            Entries.Select(e => new EgymExerciseSetCount(e.Id, 2)).ToArray(),
            Entries.Where(e => !omitGarminRow || e.Id != 333)
                .Select(e => new GarminExerciseSetCount(e.Category, e.Name, 2, true)).ToArray(),
            Entries.Select(e => new EgymExerciseWeight(e.Id, e.Egym)).ToArray(),
            Entries.SelectMany(e => Enumerable.Repeat(new GarminRecordedSet(e.Category, e.Name, e.Garmin * 1000m, true), 2)).ToArray(),
            Ids);

    [Fact]
    public void August31FixtureProducesEightReviewOnlyWeightDifferences()
    {
        var report = Build(new("egym-aug31", 24180037460, SessionMatchStatus.Matched, TimeSpan.FromMinutes(44)));
        Assert.True(report.HasReviewableDifferences);
        Assert.Equal(8, report.Differences.Count);
        Assert.Equal(-1m, report.Differences.Single(x => x.EgymActivityId == 333).DeltaKg);
        Assert.Equal(MatchStatus.Equal, report.Differences.Single(x => x.EgymActivityId == 335).Status);
        Assert.Equal(7, report.Differences.Count(x => x.Status == MatchStatus.Different));
    }

    [Fact]
    public void MethodConflictNeverGeneratesWeightDifferences()
    {
        var report = Build(new("egym-jun24", 23360196698, SessionMatchStatus.MethodConflict, TimeSpan.FromMinutes(42)));
        Assert.False(report.HasReviewableDifferences);
        Assert.Empty(report.Differences);
    }

    [Fact]
    public void IncompleteGarminActivityNeverGeneratesWeightDifferences()
    {
        var report = Build(new("egym-aug31", 24180037460, SessionMatchStatus.Matched, TimeSpan.FromMinutes(44)), true);
        Assert.False(report.HasReviewableDifferences);
        Assert.Empty(report.Differences);
        Assert.False(report.Completeness!.IsComplete);
    }
    [Fact]
    public void PositiveRepetitionMismatchIsSeparateFromWeightReview()
    {
        var report = ReadOnlyComparisonReport.Build(
            new("aug07", 23883291887, SessionMatchStatus.Matched, TimeSpan.FromMinutes(46)),
            Entries.Select(e => new EgymExerciseSetCount(e.Id, 2)).ToArray(),
            Entries.Select(e => new GarminExerciseSetCount(e.Category, e.Name, 2, true)).ToArray(),
            Entries.Select(e => new EgymExerciseWeight(e.Id, e.Egym)).ToArray(),
            Entries.SelectMany(e => Enumerable.Repeat(new GarminRecordedSet(e.Category, e.Name, e.Garmin * 1000m, true), 2)).ToArray(),
            Ids,
            egymReps: Entries.Select(e => new EgymExerciseReps(e.Id, e.Id is 332 or 334 ? [20, 19] : [20, 20])).ToArray(),
            garminReps: Entries.SelectMany(e => Enumerable.Repeat(new GarminExerciseReps(e.Category, e.Name, 20, true), 2)).ToArray());
        Assert.True(report.HasReviewableDifferences);
        Assert.False(report.RepetitionsAllEqual);
        Assert.Equal(2, report.Repetitions.Count(x => x.Status == RepetitionStatus.Different));
    }

    [Fact]
    public void NegativeIdenticalRepetitionsAreReportedEqual()
    {
        var report = ReadOnlyComparisonReport.Build(
            new("aug31", 24180037460, SessionMatchStatus.Matched, TimeSpan.FromMinutes(44)),
            Entries.Select(e => new EgymExerciseSetCount(e.Id, 2)).ToArray(),
            Entries.Select(e => new GarminExerciseSetCount(e.Category, e.Name, 2, true)).ToArray(),
            Entries.Select(e => new EgymExerciseWeight(e.Id, e.Egym)).ToArray(),
            Entries.SelectMany(e => Enumerable.Repeat(new GarminRecordedSet(e.Category, e.Name, e.Garmin * 1000m, true), 2)).ToArray(),
            Ids,
            egymReps: Entries.Select(e => new EgymExerciseReps(e.Id, [15, 15])).ToArray(),
            garminReps: Entries.SelectMany(e => Enumerable.Repeat(new GarminExerciseReps(e.Category, e.Name, 15, true), 2)).ToArray());
        Assert.True(report.RepetitionsAllEqual);
        Assert.Equal(8, report.Repetitions.Count);
    }

}
