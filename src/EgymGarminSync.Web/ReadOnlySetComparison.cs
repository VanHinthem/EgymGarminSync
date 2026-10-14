using System.Text.Json;
using EgymGarminSync.Egym;
using EgymGarminSync.Matching;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using EgymGarminSync.Garmin.Cli;

namespace EgymGarminSync.Web;

internal static class ReadOnlySetComparison
{
    private static readonly Dictionary<long, (string Name, string Category, string? Exercise)> Known = new()
    {
        [333]=("Chest Press","BENCH_PRESS",null),
        [334]=("Seated Row","ROW","SEATED_CABLE_ROW"),
        [581]=("Leg Press","SQUAT","LEG_PRESS"),
        [330]=("Abdominal Crunch","CRUNCH","WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER"),
        [335]=("Lat Pulldown","PULL_UP","LAT_PULLDOWN"),
        [329]=("Leg Extension","CRUNCH","WEIGHTED_LEG_EXTENSIONS"),
        [331]=("Back Extension","HYPEREXTENSION","WEIGHTED_STATIC_BACK_EXTENSION"),
        [332]=("Leg Curl","LEG_CURL","WEIGHTED_LEG_CURL")
    };

    public static async Task<IResult> GetAsync(string egymId, long garminId)
    {
        if (!egymId.StartsWith("egym-", StringComparison.Ordinal) ||
            !int.TryParse(egymId.AsSpan(5), out var index) || index < 0 || garminId <= 0)
            return Results.BadRequest(new { error = "Invalid session selection." });
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Windows DPAPI is required for Garmin access.", statusCode: 501);
        var ee = Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var ep = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        var ge = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var gp = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (new[] { ee, ep, ge, gp }.Any(string.IsNullOrWhiteSpace))
            return Results.Problem("EGYM/Garmin server credentials are missing.", statusCode: 503);
        try
        {
            using var egym = new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"), ee!, ep!);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var sessions = await egym.GetWorkoutsAsync(today.AddDays(-180), today);
            if (index >= sessions.Count) return Results.NotFound();
            var workout = sessions[index];

            var cache = new WindowsGarminTokenCache(ge!);
            if (cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown.", statusCode: 429);
            using var http = new HttpClient();
            var context = new GarminConnectContext(http,
                new BasicAuthParameters(ge!, gp!), new NotImplementedMfaCode(), cache);
            // Only permit IDs obtained from the recent EGYM activity list.
            var client = new GarminConnectClient(context);
            var activities = await client.GetActivities(0, 100);
            if (!activities.Any(a => a.ActivityId == garminId &&
                a.ActivityName is "EGYM Positief" or "EGYM Negatief"))
                return Results.BadRequest(new { error = "Selected Garmin activity is not a recent EGYM workout." });
            var live = await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{garminId}/exerciseSets");
            var rawSets = live.GetProperty("exerciseSets").EnumerateArray()
                .Select((item, position) => (item, position))
                .Where(x => x.item.TryGetProperty("setType", out var type) &&
                    type.GetString() == "ACTIVE" && x.item.TryGetProperty("exercises", out var exercises) &&
                    exercises.ValueKind == JsonValueKind.Array && exercises.GetArrayLength() > 0)
                .ToArray();

            var rows = new List<object>();
            var unmatched = new List<string>();
            foreach (var exercise in workout.GetProperty("exercises").EnumerateArray())
            {
                var id = exercise.GetProperty("activity").GetProperty("activityId").GetInt64();
                if (id == 31) continue; // Cycling never merged into strength sets.
                if (!Known.TryGetValue(id, out var mapping))
                {
                    unmatched.Add($"EGYM exercise {id} has no Garmin identity mapping.");
                    continue;
                }
                var sourceSets = exercise.GetProperty("sets").EnumerateArray().ToArray();
                var targets = rawSets.Where(x =>
                {
                    var first = x.item.GetProperty("exercises")[0];
                    var category = first.GetProperty("category").GetString();
                    var name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    return category == mapping.Category && name == mapping.Exercise;
                }).OrderBy(x => x.position).ToArray();
                if (sourceSets.Length != 2 || targets.Length != 2)
                {
                    unmatched.Add($"{mapping.Name}: expected two sets on both sides (EGYM {sourceSets.Length}, Garmin {targets.Length}).");
                    continue;
                }
                for (var set = 0; set < 2; set++)
                {
                    var source = sourceSets[set];
                    var target = targets[set].item;
                    rows.Add(new {
                        id = $"{id}-{targets[set].position}",
                        exercise = mapping.Name,
                        set = set + 1,
                        egymKg = source.GetProperty("weight").GetDecimal(),
                        garminKg = target.GetProperty("weight").GetDecimal() / 1000m,
                        egymReps = source.GetProperty("numberOfReps").GetInt32(),
                        garminReps = target.GetProperty("repetitionCount").GetInt32(),
                        garminSetIndex = targets[set].position
                    });
                }
            }
            cache.ClearLoginCooldown();
            return Results.Ok(new { rows, warnings = unmatched, pairing = "Physical per-exercise set order; review before any future write.", readOnly = true });
        }
        catch (Exception)
        {
            return Results.Problem("Could not compare selected workout sets. No write attempted.", statusCode: 502);
        }
    }
}
