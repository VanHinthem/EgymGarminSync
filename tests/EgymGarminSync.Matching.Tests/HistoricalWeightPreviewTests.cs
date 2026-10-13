using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class HistoricalWeightPreviewTests
{
    private static readonly SessionMatch Matched =
        new("egym-aug31", 24180037460, SessionMatchStatus.Matched, TimeSpan.FromMinutes(45));

    private static ComparisonReport Report(bool complete = true, bool reviewable = true) =>
        new(Matched, new CompletenessReport(complete, complete ? [] :
                [new CompletenessIssue(CompletenessSide.Garmin, null, 1, 0, "UnexpectedExercise")]),
            [
                new(333, 21m, 22m, new GarminExerciseKey("BENCH_PRESS", null), MatchStatus.Different),
                new(334, 32m, 31m, new GarminExerciseKey("ROW", "SEATED_CABLE_ROW"), MatchStatus.Different),
                new(581, 95m, 89m, new GarminExerciseKey("SQUAT", "LEG_PRESS"), MatchStatus.Different),
                new(330, 30m, 24m, new GarminExerciseKey("CRUNCH", "WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER"), MatchStatus.Different),
                new(335, 33m, 33m, new GarminExerciseKey("PULL_UP", "LAT_PULLDOWN"), MatchStatus.Equal),
                new(329, 22m, 20m, new GarminExerciseKey("CRUNCH", "WEIGHTED_LEG_EXTENSIONS"), MatchStatus.Different),
                new(331, 44m, 43m, new GarminExerciseKey("HYPEREXTENSION", "WEIGHTED_STATIC_BACK_EXTENSION"), MatchStatus.Different),
                new(332, 23m, 22m, new GarminExerciseKey("LEG_CURL", "WEIGHTED_LEG_CURL"), MatchStatus.Different)
            ], reviewable, "read-only");

    [Fact]
    public void Aug31ShowsSevenProposedChangesAndKeepsEqualMachineOut()
    {
        var rows = HistoricalWeightPreview.Build(Report());
        Assert.Equal(7, rows.Count);
        Assert.DoesNotContain(rows, x => x.ExerciseId == 335);
        Assert.Equal(-1m, rows.Single(x => x.ExerciseId == 333).DifferenceKg);
    }

    [Fact]
    public void ExtraUnknownAndUnreviewableAreBlocked()
    {
        Assert.Empty(HistoricalWeightPreview.Build(Report(complete: false)));
        Assert.Empty(HistoricalWeightPreview.Build(Report(reviewable: false)));
    }

    [Fact]
    public void ConflictedSessionIsBlocked()
    {
        var r = Report();
        Assert.Empty(HistoricalWeightPreview.Build(r with
        {
            Session = new("egym-jun24", 23360196698, SessionMatchStatus.MethodConflict, TimeSpan.FromMinutes(45))
        }));
    }
}
