using System.Globalization;
using System.Text.Json;
using EgymGarminSync.Egym;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Read-only historical session candidate report; no activity set fetches or writes.</summary>
internal static class HistoricalMatchingDiagnostic
{
    public static async Task<int> RunAsync(GarminConnectClient garmin, int days)
    {
        var email = Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var password = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Set EGYM_EMAIL and EGYM_PASSWORD for historical matching.");
            return 1;
        }

        using var egym = new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"), email, password);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var workouts = await egym.GetWorkoutsAsync(today.AddDays(-days), today);
        var activities = await garmin.GetActivities(0, 100);
        var source = new List<EgymWorkoutSession>();
        foreach (var (workout, index) in workouts.Select((item, index) => (item, index)))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(RequiredString(workout, "timezone"));
                var local = DateTime.SpecifyKind(DateTime.Parse(RequiredString(workout, "completedAt"),
                    CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
                if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
                    throw new FormatException("Ambiguous or invalid local completion timestamp.");
                var completionUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
                var methods = workout.GetProperty("exercises").EnumerateArray()
                    .Where(e => e.TryGetProperty("sets", out _))
                    .SelectMany(e => e.GetProperty("sets").EnumerateArray())
                    .Where(s => s.TryGetProperty("trainingMethod", out var method) &&
                        method.ValueKind == JsonValueKind.String)
                    .Select(s => s.GetProperty("trainingMethod").GetString())
                    .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
                var method = methods.Length == 1 ? methods[0] switch
                {
                    "REGULAR_REPBASED" => EgymTrainingMethod.RegularRepBased,
                    "NEGATIVE_REPBASED" => EgymTrainingMethod.NegativeRepBased,
                    _ => EgymTrainingMethod.Unknown
                } : EgymTrainingMethod.Unknown;
                // Local enumeration identity is only for this one read-only report.
                source.Add(new($"egym-{index}-{completionUtc:yyyyMMddTHHmmssfff}", completionUtc, method));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                Console.WriteLine($"  EGYM record {index}: could not normalize ({ex.GetType().Name}); excluded, not guessed.");
            }
        }

        var target = activities.Where(a => a.ActivityName is "EGYM Positief" or "EGYM Negatief")
            .Select(a => new GarminWorkoutSession(a.ActivityId,
                new DateTimeOffset(DateTime.SpecifyKind(a.StartTimeGmt, DateTimeKind.Utc)),
                a.ActivityName == "EGYM Positief" ? GarminTrainingVariant.Positive : GarminTrainingVariant.Negative))
            .ToArray();

        var results = WorkoutSessionMatcher.Match(source, target);
        Console.WriteLine($"Historical GET-only candidates: EGYM normalized={source.Count}/{workouts.Count}; Garmin recent EGYM={target.Length}; window=0..120 min.");
        foreach (var result in results.OrderByDescending(r => source.Single(e => e.Id == r.EgymWorkoutId).CompletedAtUtc))
        {
            var e = source.Single(x => x.Id == result.EgymWorkoutId);
            Console.WriteLine($"  {e.CompletedAtUtc:yyyy-MM-dd HH:mm}Z {e.Method}: {result.Status}" +
                $" Garmin={result.GarminActivityId?.ToString() ?? "—"} elapsed={result.TimeDifference?.ToString() ?? "—"}");
        }
        Console.WriteLine($"Summary: Matched={results.Count(r => r.Status == SessionMatchStatus.Matched)}; " +
            $"MethodConflict={results.Count(r => r.Status == SessionMatchStatus.MethodConflict)}; " +
            $"Ambiguous={results.Count(r => r.Status == SessionMatchStatus.Ambiguous)}; " +
            $"NoCandidate={results.Count(r => r.Status == SessionMatchStatus.NoCandidate)}; " +
            $"UnsupportedMethod={results.Count(r => r.Status == SessionMatchStatus.UnsupportedMethod)}.");
        Console.WriteLine("This reports session candidates only. No exercise-set writes or weight corrections.");
        return 0;
    }

    private static string RequiredString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new JsonException($"Missing {key}.");
}
