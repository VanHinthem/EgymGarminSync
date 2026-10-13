using EgymGarminSync.Matching;
using Xunit;

namespace EgymGarminSync.Matching.Tests;

public sealed class WorkoutSessionMatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 31, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UniqueCompatibleSessionMatches()
    {
        var results = WorkoutSessionMatcher.Match(
            [new("egym-1", T0.AddHours(1), EgymTrainingMethod.NegativeRepBased)],
            [new(24180037460, T0, GarminTrainingVariant.Negative)]);
        var match = Assert.Single(results);
        Assert.Equal(SessionMatchStatus.Matched, match.Status);
        Assert.Equal(24180037460, match.GarminActivityId);
        Assert.Equal(TimeSpan.FromHours(1), match.TimeDifference);
    }

    [Fact]
    public void DifferentMethodNeverMatchesEvenOnSameDate()
    {
        var match = Assert.Single(WorkoutSessionMatcher.Match(
            [new("egym-1", T0, EgymTrainingMethod.RegularRepBased)],
            [new(11, T0, GarminTrainingVariant.Negative)]));
        Assert.Equal(SessionMatchStatus.NoCandidate, match.Status);
    }

    [Fact]
    public void OutsideTimeWindowIsNotMatched()
    {
        var match = Assert.Single(WorkoutSessionMatcher.Match(
            [new("egym-1", T0, EgymTrainingMethod.NegativeRepBased)],
            [new(11, T0.AddHours(5), GarminTrainingVariant.Negative)]));
        Assert.Equal(SessionMatchStatus.NoCandidate, match.Status);
    }

    [Fact]
    public void MultipleGarminCandidatesAreAmbiguous()
    {
        var match = Assert.Single(WorkoutSessionMatcher.Match(
            [new("egym-1", T0, EgymTrainingMethod.NegativeRepBased)],
            [new(11, T0.AddMinutes(10), GarminTrainingVariant.Negative),
             new(12, T0.AddMinutes(20), GarminTrainingVariant.Negative)]));
        Assert.Equal(SessionMatchStatus.Ambiguous, match.Status);
        Assert.Null(match.GarminActivityId);
    }

    [Fact]
    public void SharedGarminCandidateIsAmbiguousForBothEgymSessions()
    {
        var matches = WorkoutSessionMatcher.Match(
            [new("egym-1", T0, EgymTrainingMethod.NegativeRepBased),
             new("egym-2", T0.AddMinutes(30), EgymTrainingMethod.NegativeRepBased)],
            [new(11, T0.AddMinutes(15), GarminTrainingVariant.Negative)]);
        Assert.All(matches, match => Assert.Equal(SessionMatchStatus.Ambiguous, match.Status));
    }

    [Fact]
    public void UnknownMethodIsExplicitlyUnsupported()
    {
        var match = Assert.Single(WorkoutSessionMatcher.Match(
            [new("egym-1", T0, EgymTrainingMethod.Unknown)],
            [new(11, T0, GarminTrainingVariant.Positive)]));
        Assert.Equal(SessionMatchStatus.UnsupportedMethod, match.Status);
    }

    [Fact]
    public void NonUtcInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => WorkoutSessionMatcher.Match(
            [new("egym-1", T0.ToOffset(TimeSpan.FromHours(2)), EgymTrainingMethod.NegativeRepBased)],
            []));
    }
}
