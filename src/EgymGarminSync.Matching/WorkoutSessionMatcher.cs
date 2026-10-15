namespace EgymGarminSync.Matching;

/// <summary>
/// Conservative, read-only session association. Inputs must use normalized UTC timestamps.
/// Never links workouts solely by list order or by calendar date.
/// </summary>
public static class WorkoutSessionMatcher
{
    // Provisional candidate window based on observed ~44-47-minute start-to-completion gaps.
    // Not yet a validated production matching threshold.
    private static readonly TimeSpan DefaultTolerance = TimeSpan.FromHours(2);

    public static IReadOnlyList<SessionMatch> Match(
        IReadOnlyCollection<EgymWorkoutSession> egym,
        IReadOnlyCollection<GarminWorkoutSession> garmin,
        TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(egym);
        ArgumentNullException.ThrowIfNull(garmin);
        var threshold = tolerance ?? DefaultTolerance;
        if (threshold <= TimeSpan.Zero || threshold > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be between 0 and 24 hours.");

        if (egym.Any(x => x.CompletedAtUtc.Offset != TimeSpan.Zero) ||
            garmin.Any(x => x.StartedAtUtc.Offset != TimeSpan.Zero))
            throw new ArgumentException("Normalize all session timestamps to UTC before matching.");

        if (egym.Any(x => string.IsNullOrWhiteSpace(x.Id)) ||
            garmin.Any(x => x.Id <= 0) ||
            egym.GroupBy(x => x.Id).Any(x => x.Count() > 1) ||
            garmin.GroupBy(x => x.Id).Any(x => x.Count() > 1))
            throw new ArgumentException("Session IDs must be present and unique.");

        var results = new List<SessionMatch>();
        // Completion must follow Garmin start. Retain ALL nearby sessions before
        // checking method; otherwise a mislabeled session could be silently ignored.
        var candidates = egym.ToDictionary(
            e => e.Id,
            e => garmin.Where(g =>
            {
                var elapsed = e.CompletedAtUtc - g.StartedAtUtc;
                return elapsed >= TimeSpan.Zero && elapsed <= threshold;
            }).ToArray());

        foreach (var e in egym)
        {
            if (!IsSupportedMethod(e.Method))
            {
                results.Add(new(e.Id, null, SessionMatchStatus.UnsupportedMethod, null));
                continue;
            }

            var nearby = candidates[e.Id];
            if (nearby.Length == 0)
            {
                results.Add(new(e.Id, null, SessionMatchStatus.NoCandidate, null));
                continue;
            }
            // Do not auto-pick one of several sessions, even if only one
            // variant agrees. A mislabeled workout needs explicit review.
            if (nearby.Length > 1 ||
                candidates.Count(pair => pair.Value.Any(g => g.Id == nearby[0].Id)) != 1)
            {
                results.Add(new(e.Id, null, SessionMatchStatus.Ambiguous, null));
                continue;
            }

            var candidate = nearby[0];
            var elapsed = e.CompletedAtUtc - candidate.StartedAtUtc;
            if (!Compatible(e.Method, candidate.Variant))
            {
                results.Add(new(e.Id, candidate.Id, SessionMatchStatus.MethodConflict, elapsed));
                continue;
            }
            results.Add(new(e.Id, candidate.Id, SessionMatchStatus.Matched, elapsed));
        }
        return results;
    }

    private static bool Compatible(EgymTrainingMethod method, GarminTrainingVariant variant) =>
        method switch
        {
            EgymTrainingMethod.RegularRepBased => variant == GarminTrainingVariant.Positive,
            EgymTrainingMethod.NegativeRepBased => variant == GarminTrainingVariant.Negative,
            _ => false
        };

    private static bool IsSupportedMethod(EgymTrainingMethod method) =>
        method is EgymTrainingMethod.RegularRepBased or EgymTrainingMethod.NegativeRepBased;

}

public enum EgymTrainingMethod { Unknown, RegularRepBased, NegativeRepBased }
public enum GarminTrainingVariant { Unknown, Positive, Negative }
public sealed record EgymWorkoutSession(string Id, DateTimeOffset CompletedAtUtc, EgymTrainingMethod Method);
public sealed record GarminWorkoutSession(long Id, DateTimeOffset StartedAtUtc, GarminTrainingVariant Variant);
public enum SessionMatchStatus { Matched, NoCandidate, Ambiguous, UnsupportedMethod, MethodConflict }
public sealed record SessionMatch(
    string EgymWorkoutId, long? GarminActivityId, SessionMatchStatus Status, TimeSpan? TimeDifference);
