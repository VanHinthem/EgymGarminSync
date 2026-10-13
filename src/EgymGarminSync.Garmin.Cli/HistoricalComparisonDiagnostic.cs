using System.Globalization;
using System.Text.Json;
using EgymGarminSync.Egym;
using EgymGarminSync.Matching;
using Garmin.Connect;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>GET-only historical review. Never sends corrections to Garmin.</summary>
internal static class HistoricalComparisonDiagnostic
{
    private static readonly long[] Ids = [333, 334, 581, 330, 335, 329, 331, 332];

    public static async Task<int> RunAsync(GarminConnectContext context, GarminConnectClient garmin, int maxSessions = 27)
    {
        var email = Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var password = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Set EGYM_EMAIL and EGYM_PASSWORD.");
            return 1;
        }

        using var egym = new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"), email, password);
        var end = DateOnly.FromDateTime(DateTime.Today);
        var workouts = await egym.GetWorkoutsAsync(end.AddDays(-180), end);
        var source = new List<EgymWorkoutSession>();
        var byId = new Dictionary<string, JsonElement>();
        foreach (var (workout, i) in workouts.Select((value, i) => (value, i)))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(Required(workout, "timezone"));
                var local = DateTime.SpecifyKind(DateTime.Parse(Required(workout, "completedAt"),
                    CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                    throw new FormatException("Invalid local time");
                var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
                var methods = workout.GetProperty("exercises").EnumerateArray()
                    .SelectMany(ex => ex.GetProperty("sets").EnumerateArray())
                    .Where(set => set.TryGetProperty("trainingMethod", out var m) && m.ValueKind == JsonValueKind.String)
                    .Select(set => set.GetProperty("trainingMethod").GetString())
                    .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
                var method = methods.Length == 1 ? methods[0] switch
                {
                    "REGULAR_REPBASED" => EgymTrainingMethod.RegularRepBased,
                    "NEGATIVE_REPBASED" => EgymTrainingMethod.NegativeRepBased,
                    _ => EgymTrainingMethod.Unknown
                } : EgymTrainingMethod.Unknown;
                var id = $"egym-{i}-{utc:yyyyMMddTHHmmssfff}";
                source.Add(new(id, utc, method));
                byId.Add(id, workout);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                Console.WriteLine($"EGYM record {i}: invalid timestamp or method structure ({ex.GetType().Name}), skipped.");
            }
        }

        var activities = await garmin.GetActivities(0, 100);
        var targets = activities.Where(a => a.ActivityName is "EGYM Positief" or "EGYM Negatief")
            .Select(a => new GarminWorkoutSession(a.ActivityId,
                new DateTimeOffset(DateTime.SpecifyKind(a.StartTimeGmt, DateTimeKind.Utc)),
                a.ActivityName == "EGYM Positief" ? GarminTrainingVariant.Positive : GarminTrainingVariant.Negative))
            .ToArray();
        var matches = WorkoutSessionMatcher.Match(source, targets)
            .OrderByDescending(m => source.Single(e => e.Id == m.EgymWorkoutId).CompletedAtUtc).ToArray();
        Console.WriteLine($"Historical review: EGYM={source.Count}, Garmin={targets.Length}; matched={matches.Count(m => m.Status == SessionMatchStatus.Matched)}; conflicts={matches.Count(m => m.Status == SessionMatchStatus.MethodConflict)}.");

        var visited = 0;
        var failures = 0;
        foreach (var match in matches)
        {
            var session = source.Single(s => s.Id == match.EgymWorkoutId);
            var date = session.CompletedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (match.Status != SessionMatchStatus.Matched || match.GarminActivityId is null)
            {
                Console.WriteLine($"{date} {match.Status}: no exercise comparisons.");
                continue;
            }
            if (visited >= maxSessions) continue;
            visited++;
            try
            {
                var workout = byId[session.Id];
                var egymCounts = CircuitJsonNormalizer.EgymCounts(workout);
                var egymReps = RepetitionJsonNormalizer.Egym(workout);
                var egymWeights = new List<EgymExerciseWeight>();
                var egymWeightConflict = false;
                foreach (var ex in workout.GetProperty("exercises").EnumerateArray())
                {
                    var id = ex.GetProperty("activity").GetProperty("activityId").GetInt64();
                    if (id == 31) continue;
                    var weights = ex.GetProperty("sets").EnumerateArray()
                        .Select(set => set.GetProperty("weight").GetDecimal()).Distinct().ToArray();
                    if (weights.Length != 1) { egymWeightConflict = true; break; }
                    egymWeights.Add(new(id, weights[0]));
                }
                var raw = await context.GetAndDeserialize<JsonElement>(
                    $"/activity-service/activity/{match.GarminActivityId}/exerciseSets");
                var garminCounts = CircuitJsonNormalizer.GarminCounts(raw);
                var garminReps = RepetitionJsonNormalizer.Garmin(raw);
                var recorded = new List<GarminRecordedSet>();
                foreach (var set in raw.GetProperty("exerciseSets").EnumerateArray())
                {
                    if (set.GetProperty("setType").GetString() != "ACTIVE") continue;
                    var first = set.GetProperty("exercises")[0];
                    var category = Required(first, "category");
                    if (category == "INDOOR_BIKE") continue;
                    var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    decimal? grams = set.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDecimal() : null;
                    recorded.Add(new(category, name, grams, true));
                }

                if (egymWeightConflict)
                {
                    Console.WriteLine($"{date} Garmin={match.GarminActivityId} EGYM weights vary between sets; requires per-set review.");
                    PrintSetDetails(workout, raw);
                    continue;
                }
                var report = ReadOnlyComparisonReport.Build(match, egymCounts, garminCounts,
                    egymWeights, recorded, Ids, egymReps: egymReps, garminReps: garminReps);
                if (garminReps.Any(r => r.Repetitions == 0))
                {
                    Console.WriteLine("  ZERO-REP Garmin ACTIVE strength set detected; raw details:");
                    PrintSetDetails(workout, raw);
                }
                if (report.Completeness is { IsComplete: false } incomplete)
                {
                    foreach (var issue in incomplete.Issues)
                        Console.WriteLine($"  COMPLETENESS {issue.Side} exercise={issue.EgymActivityId?.ToString() ?? "unknown"} actual={issue.ActualSets} expected={issue.ExpectedSets} reason={issue.Reason}");
                    PrintSetDetails(workout, raw);
                }
                var repChanges = report.Repetitions.Count(r => r.Status == RepetitionStatus.Different);
                var weightChanges = report.Differences.Count(d => d.Status == MatchStatus.Different);
                Console.WriteLine($"{date} Garmin={match.GarminActivityId} complete={report.Completeness?.IsComplete} " +
                    $"repsEqual={report.RepetitionsAllEqual} repDifferences={repChanges} " +
                    $"weightReviewable={report.HasReviewableDifferences} weightDifferences={weightChanges}");
                foreach (var rep in report.Repetitions.Where(r => r.Status != RepetitionStatus.Equal))
                    Console.WriteLine($"  REPS exercise={rep.EgymActivityId} EGYM=[{string.Join(",",rep.EgymReps)}] Garmin=[{string.Join(",",rep.GarminReps)}] {rep.Status}");
                foreach (var weight in report.Differences.Where(d => d.Status != MatchStatus.Equal))
                    Console.WriteLine($"  WEIGHT exercise={weight.EgymActivityId} EGYM={weight.EgymKg} Garmin={weight.GarminKg} delta={weight.DeltaKg} {weight.Status}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                failures++;
                Console.WriteLine($"{date} Garmin={match.GarminActivityId} cannot compare ({ex.GetType().Name}); no correction inferred.");
            }
        }
        Console.WriteLine($"Inspected={visited}; structural errors={failures}; no data written.");
        return failures == 0 ? 0 : 1;
    }

    private static void PrintSetDetails(JsonElement workout, JsonElement garmin)
    {
        Console.WriteLine("  SET DETAILS (read-only, physical order; no cross-system set pairing):");
        foreach (var exercise in workout.GetProperty("exercises").EnumerateArray())
        {
            var id = exercise.GetProperty("activity").GetProperty("activityId").GetInt64();
            if (id == 31) continue;
            var sets = exercise.GetProperty("sets").EnumerateArray().ToArray();
            Console.WriteLine($"  EGYM exercise={id} sets={sets.Length} " +
                string.Join("; ", sets.Select((set, i) => $"#{i + 1} reps={RawNumber(set, "numberOfReps")} weight={RawNumber(set, "weight")}")));
        }
        foreach (var (set, index) in garmin.GetProperty("exerciseSets").EnumerateArray().Select((set, index) => (set, index)))
        {
            if (set.GetProperty("setType").GetString() != "ACTIVE") continue;
            var exercises = set.GetProperty("exercises").EnumerateArray().ToArray();
            if (exercises.Length == 0) { Console.WriteLine($"  Garmin record={index} ACTIVE has no identity"); continue; }
            var first = exercises[0];
            var category = Required(first, "category");
            if (category == "INDOOR_BIKE") continue;
            var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            Console.WriteLine($"  Garmin record={index} {category}/{name ?? "(none)"} reps={RawNumber(set, "repetitionCount")} weightRaw={RawNumber(set, "weight")}");
        }
    }

    private static string RawNumber(JsonElement item, string field) =>
        item.TryGetProperty(field, out var value) ? value.ToString() : "(missing)";

    private static string Required(JsonElement value, string field) =>
        value.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()! : throw new JsonException($"Missing {field}.");
}
