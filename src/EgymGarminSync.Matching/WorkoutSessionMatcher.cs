namespace EgymGarminSync.Matching;

/// <summary>
/// Conservative, read-only session association. Inputs must use normalized UTC timestamps.
/// Never links workouts solely by list order or by calendar date.
/// </summary>
public static class WorkoutSessionMatcher
{
    private static readonly TimeSpan DefaultTolerance = TimeSpan.FromHours(4);

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
        // Build a bipartite candidate graph: matching variant AND plausible session time.
        // A candidate is safe only when it is unique in both directions.
        var candidates = egym.ToDictionary(
            e => e.Id,
            e => garmin.Where(g => Compatible(e.Method, g.Variant) &&
                                 AbsoluteDifference(e.CompletedAtUtc, g.StartedAtUtc) <= threshold)
                       .Select(g => g.Id).ToArray());

        foreach (var e in egym)
        {
            if (!IsSupportedMethod(e.Method))
            {
                results.Add(new(e.Id, null, SessionMatchStatus.UnsupportedMethod, null));
                continue;
            }

            var matches = candidates[e.Id];
            if (matches.Length == 0)
            {
                results.Add(new(e.Id, null, SessionMatchStatus.NoCandidate, null));
                continue;
            }
            if (matches.Length > 1 || candidates.Count(pair => pair.Value.Contains(matches[0])) != 1)
            {
                results.Add(new(e.Id, null, SessionMatchStatus.Ambiguous, null));
                continue;
            }
            var candidate = garmin.Single(g => g.Id == matches[0]);
            results.Add(new(e.Id, candidate.Id, SessionMatchStatus.Matched,
                AbsoluteDifference(e.CompletedAtUtc, candidate.StartedAtUtc)));
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

    private static TimeSpan AbsoluteDifference(DateTimeOffset a, DateTimeOffset b) =>
        (a - b).Duration();
}

public enum EgymTrainingMethod { Unknown, RegularRepBased, NegativeRepBased }
public enum GarminTrainingVariant { Unknown, Positive, Negative }
public sealed record EgymWorkoutSession(string Id, DateTimeOffset CompletedAtUtc, EgymTrainingMethod Method);
public sealed record GarminWorkoutSession(long Id, DateTimeOffset StartedAtUtc, GarminTrainingVariant Variant);
public enum SessionMatchStatus { Matched, NoCandidate, Ambiguous, UnsupportedMethod }
public sealed record SessionMatch(
    string EgymWorkoutId, long? GarminActivityId, SessionMatchStatus Status, TimeSpan? TimeDifference);
