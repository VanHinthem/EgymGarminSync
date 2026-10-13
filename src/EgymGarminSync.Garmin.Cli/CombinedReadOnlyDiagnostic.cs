using System.Globalization;
using System.Text.Json;
using EgymGarminSync.Egym;
using EgymGarminSync.Matching;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Parameters;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>One known historical session: GET-only comparison, never a Garmin update.</summary>
internal static class CombinedReadOnlyDiagnostic
{
    private static readonly long[] MachineIds = [333, 334, 581, 330, 335, 329, 331, 332];

    public static async Task<int> RunAsync(GarminConnectContext context, GarminConnectClient garmin, DateOnly trainingDate, long garminActivityId)
    {
        var email = Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var password = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Combined read-only comparison requires EGYM_EMAIL and EGYM_PASSWORD.");
            return 1;
        }

        Console.WriteLine($"Comparing {trainingDate:yyyy-MM-dd} (GET only; all weights are provisional observations).");
        using var egym = new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"), email, password);
        var workouts = await egym.GetWorkoutsAsync(trainingDate, trainingDate);
        // Require exactly one full candidate on this local calendar date.
        var candidates = workouts.Where(w => ParseLocalDate(w) == trainingDate).ToArray();
        if (candidates.Length != 1)
        {
            Console.WriteLine($"EGYM sessions on target date: {candidates.Length}; refusing ambiguous association.");
            return 1;
        }
        var workout = candidates[0];
        var tzId = ReadString(workout, "timezone");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        var local = DateTime.Parse(ReadString(workout, "completedAt"), CultureInfo.InvariantCulture, DateTimeStyles.None);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new InvalidOperationException("Ambiguous EGYM local time: cannot match safely.");
        var completionUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);

        var egymCounts = CircuitJsonNormalizer.EgymCounts(workout);
        var weights = new List<EgymExerciseWeight>();
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exercise in workout.GetProperty("exercises").EnumerateArray())
        {
            var id = exercise.GetProperty("activity").GetProperty("activityId").GetInt64();
            if (id == 31) continue;
            var setWeights = new HashSet<decimal>();
            foreach (var set in exercise.GetProperty("sets").EnumerateArray())
            {
                methods.Add(ReadString(set, "trainingMethod"));
                setWeights.Add(ReadDecimal(set.GetProperty("weight")));
            }
            if (setWeights.Count != 1)
            {
                Console.WriteLine($"EGYM equipment {id} has different weights per set; refusing aggregated comparison.");
                return 1;
            }
            weights.Add(new(id, setWeights.Single()));
        }

        var method = methods.Count == 1 ? methods.Single() switch
        {
            "NEGATIVE_REPBASED" => EgymTrainingMethod.NegativeRepBased,
            "REGULAR_REPBASED" => EgymTrainingMethod.RegularRepBased,
            _ => EgymTrainingMethod.Unknown
        } : EgymTrainingMethod.Unknown;

        var activities = await garmin.GetActivities(0, 100);
        var activity = activities.SingleOrDefault(a => a.ActivityId == garminActivityId);
        if (activity is null)
        {
            Console.WriteLine("Known Garmin activity not found in recent-activity list; no comparison.");
            return 1;
        }
        var variant = activity.ActivityName switch
        {
            "EGYM Negatief" => GarminTrainingVariant.Negative,
            "EGYM Positief" => GarminTrainingVariant.Positive,
            _ => GarminTrainingVariant.Unknown
        };
        var startUtc = new DateTimeOffset(DateTime.SpecifyKind(activity.StartTimeGmt, DateTimeKind.Utc));
        var session = WorkoutSessionMatcher.Match(
            [new EgymWorkoutSession(completionUtc.ToString("O", CultureInfo.InvariantCulture), completionUtc, method)],
            [new GarminWorkoutSession(activity.ActivityId, startUtc, variant)]).Single();
        Console.WriteLine($"Session: {session.Status}; Garmin ID={session.GarminActivityId}; elapsed={session.TimeDifference}");

        if (session.Status != SessionMatchStatus.Matched)
        {
            Console.WriteLine("No weight comparison for an unmatched or disputed session.");
            return 0;
        }

        var raw = await context.GetAndDeserialize<JsonElement>(
            $"/activity-service/activity/{garminActivityId}/exerciseSets");
        var garminCounts = CircuitJsonNormalizer.GarminCounts(raw);
        var repDiffs = RepetitionMatcher.Compare(
            RepetitionJsonNormalizer.Egym(workout), RepetitionJsonNormalizer.Garmin(raw));
        Console.WriteLine("Repetitions per strength exercise (individual sets; no sequence assumption):");
        foreach (var reps in repDiffs.OrderBy(x => x.EgymActivityId))
            Console.WriteLine($"  EGYM id={reps.EgymActivityId}: EGYM=[{string.Join(",", reps.EgymReps)}], Garmin=[{string.Join(",", reps.GarminReps)}], status={reps.Status}");
        Console.WriteLine($"Repetitions all equal: {repDiffs.Count == MachineIds.Length && repDiffs.All(x => x.Status == RepetitionStatus.Equal)}");
        var recorded = new List<GarminRecordedSet>();
        foreach (var set in raw.GetProperty("exerciseSets").EnumerateArray())
        {
            if (ReadString(set, "setType") != "ACTIVE") continue;
            var exercises = set.GetProperty("exercises");
            if (exercises.GetArrayLength() == 0) throw new JsonException("Missing Garmin exercise identity.");
            var first = exercises[0];
            var category = ReadString(first, "category");
            if (category == "INDOOR_BIKE") continue;
            var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            decimal? grams = set.TryGetProperty("weight", out var weight) &&
                             weight.ValueKind == JsonValueKind.Number ? ReadDecimal(weight) : null;
            recorded.Add(new(category, name, grams, true));
        }

        var report = ReadOnlyComparisonReport.Build(session, egymCounts, garminCounts,
            weights, recorded, MachineIds);
        Console.WriteLine($"Complete={report.Completeness?.IsComplete}; reviewable={report.HasReviewableDifferences}; {report.Reason}");
        foreach (var difference in report.Differences.OrderBy(x => x.EgymActivityId))
            Console.WriteLine($"  EGYM id={difference.EgymActivityId}: EGYM={difference.EgymKg} kg, " +
                $"Garmin={difference.GarminKg} kg, delta={difference.DeltaKg} kg, status={difference.Status}");
        Console.WriteLine("No Garmin or EGYM writes. Negative-method resistance semantics still need verification.");
        return report.HasReviewableDifferences ? 0 : 1;
    }

    private static DateOnly ParseLocalDate(JsonElement workout) =>
        DateOnly.FromDateTime(DateTime.Parse(ReadString(workout, "completedAt"), CultureInfo.InvariantCulture));

    private static string ReadString(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new JsonException($"Missing required string: {property}");
        return value.GetString()!;
    }

    private static decimal ReadDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetDecimal() :
        throw new JsonException("Expected numeric exercise weight.");
}
