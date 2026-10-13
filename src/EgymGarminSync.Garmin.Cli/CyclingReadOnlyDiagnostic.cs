using System.Globalization;
using System.Text.Json;
using EgymGarminSync.Egym;
using Garmin.Connect;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Inspect cycling segments only. Never changes Garmin or EGYM data.</summary>
internal static class CyclingReadOnlyDiagnostic
{
    public static async Task<int> RunAsync(GarminConnectContext context)
    {
        var email = Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var password = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Set EGYM_EMAIL and EGYM_PASSWORD.");
            return 1;
        }

        var date = new DateOnly(2026, 8, 31);
        using var client = new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"), email, password);
        var workouts = await client.GetWorkoutsAsync(date, date);
        var candidates = workouts.Where(w =>
            DateOnly.FromDateTime(DateTime.Parse(w.GetProperty("completedAt").GetString()!, CultureInfo.InvariantCulture)) == date).ToArray();
        if (candidates.Length != 1)
        {
            Console.WriteLine($"Found {candidates.Length} EGYM sessions on {date:yyyy-MM-dd}; aborting.");
            return 1;
        }

        var workout = candidates[0];
        Console.WriteLine("EGYM cycling entries (2026-08-31):");
        var bikes = workout.GetProperty("exercises").EnumerateArray()
            .Where(x => x.GetProperty("activity").GetProperty("activityId").GetInt64() == 31).ToArray();
        Console.WriteLine($"  Bike segments={bikes.Length}");
        foreach (var (bike, index) in bikes.Select((x, i) => (x, i)))
        {
            Console.WriteLine($"  EGYM bike segment {index + 1}:");
            PrintFields(bike, ["completedAt", "kiloCalories", "summary", "hrmData"]);
            if (bike.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                Console.WriteLine("    EGYM summary values (raw; units not assumed):");
                PrintFields(summary, ["totalDuration", "totalDistance", "verticalDistance",
                    "avgPace", "avgHeartRate", "maxHeartRate", "avgSpeed", "avgIncline"]);
            }
            var sets = bike.GetProperty("sets").EnumerateArray().ToArray();
            Console.WriteLine($"    sets={sets.Length}");
            PrintRawSum(sets, "duration");
            PrintRawSum(sets, "distance");
            foreach (var (set, setIndex) in sets.Select((x, i) => (x, i)))
            {
                Console.Write($"    set {setIndex + 1}: ");
                PrintFields(set, ["setType", "duration", "distance", "speed", "heartRate", "incline", "recoveryTime", "numberOfReps", "weight"]);
            }
        }

        var response = await context.GetAndDeserialize<JsonElement>(
            "/activity-service/activity/24180037460/exerciseSets");
        Console.WriteLine("Garmin INDOOR_BIKE ACTIVE entries (activity 24180037460):");
        var matched = 0;
        foreach (var (set, index) in response.GetProperty("exerciseSets").EnumerateArray().Select((x, i) => (x, i)))
        {
            if (set.GetProperty("setType").GetString() != "ACTIVE") continue;
            if (!set.TryGetProperty("exercises", out var identities) || identities.ValueKind != JsonValueKind.Array) continue;
            if (!identities.EnumerateArray().Any(e =>
                    e.TryGetProperty("category", out var category) && category.GetString() == "INDOOR_BIKE")) continue;
            matched++;
            Console.WriteLine($"  Garmin bike record {index}:");
            PrintFields(set, ["setType", "startTime", "duration", "durationInSeconds", "distance", "distanceInMeters",
                "speed", "averageSpeed", "heartRate", "averageHeartRate", "calories", "power", "watts", "repetitionCount", "weight"]);
            Console.WriteLine("    available scalar field names: " +
                string.Join(", ", set.EnumerateObject().Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    .Select(p => p.Name)));
        }
        Console.WriteLine("Garmin activity-level summary (GET only, full training rather than bike-specific):");
        try
        {
            var activity = await context.GetAndDeserialize<JsonElement>(
                "/activity-service/activity/24180037460");
            var fields = new[] { "duration", "durationInSeconds", "distance", "distanceInMeters",
                "averageSpeed", "avgSpeed", "maxSpeed", "calories", "kilocalories",
                "startTimeGMT", "startTimeLocal", "activityId", "activityName" };
            if (activity.ValueKind == JsonValueKind.Object)
            {
                PrintFields(activity, fields);
                foreach (var nested in new[] { "summaryDTO", "activitySummary", "summary" })
                    if (activity.TryGetProperty(nested, out var value) && value.ValueKind == JsonValueKind.Object)
                    {
                        Console.WriteLine($"  {nested} selected fields:");
                        PrintFields(value, fields);
                    }
                Console.WriteLine("  candidate field names: " +
                    string.Join(", ", activity.EnumerateObject()
                        .Where(p => p.Name.Contains("distance", StringComparison.OrdinalIgnoreCase) ||
                                    p.Name.Contains("calor", StringComparison.OrdinalIgnoreCase) ||
                                    p.Name.Contains("speed", StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Name).Take(25)));
            }
            else Console.WriteLine("  Unrecognized activity response shape.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException)
        {
            Console.WriteLine($"  Garmin activity summary not available ({ex.GetType().Name}); no conclusion inferred.");
        }
        Console.WriteLine($"  Garmin bike records={matched}");
        Console.WriteLine("Values are raw, with units unverified. No automatic segment pairing or writes.");
        return bikes.Length == 2 && matched == 2 ? 0 : 1;
    }

    private static void PrintRawSum(JsonElement[] sets, string field)
    {
        var values = sets.Select(s => s.TryGetProperty(field, out var v) &&
            v.ValueKind == JsonValueKind.Number ? (decimal?)v.GetDecimal() : null).ToArray();
        if (values.Any(v => v is null))
        {
            Console.WriteLine($"    sum({field})=unavailable (missing/non-numeric set value)");
            return;
        }
        Console.WriteLine($"    sum({field})={values.Sum(v => v!.Value).ToString(CultureInfo.InvariantCulture)} (raw units)");
    }

    private static void PrintFields(JsonElement source, string[] fields)
    {
        foreach (var key in fields)
        {
            if (!source.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                Console.WriteLine($"    {key}: structured ({value.ValueKind}); fields=" +
                    (value.ValueKind == JsonValueKind.Object
                        ? string.Join(",", value.EnumerateObject().Select(p => p.Name).Take(15))
                        : $"array length={value.GetArrayLength()}"));
            }
            else
                Console.WriteLine($"    {key}={value}");
        }
    }
}
