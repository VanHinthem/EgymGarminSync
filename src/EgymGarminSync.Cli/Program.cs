using EgymGarminSync.Matching;
using EgymGarminSync.Egym;

var email = Environment.GetEnvironmentVariable("EGYM_EMAIL");
var password = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
var baseUrl = Environment.GetEnvironmentVariable("EGYM_BASE_URL");
if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("Set EGYM_EMAIL and EGYM_PASSWORD; EGYM_BASE_URL is optional (automatically discovered).");
    return 1;
}

try
{
    using var client = new EgymClient(baseUrl, email, password);
    var today = DateOnly.FromDateTime(DateTime.Today);
    var matchingTimes = args.Contains("--matching-times", StringComparer.OrdinalIgnoreCase);
    var days = 180;
    var daysArg = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
    if (daysArg is not null && (!int.TryParse(daysArg, out days) || days is < 1 or > 730))
    {
        Console.Error.WriteLine("Optional argument: number of days to look back (1-730).");
        return 1;
    }
    var start = today.AddDays(-days);
    var workouts = await client.GetWorkoutsAsync(start, today);
    Console.WriteLine($"Retrieved {workouts.Count} workouts in the past {days} days.");
    // Only summary metadata; do not dump sensitive raw workout payloads to the terminal.
    var sorted = workouts.OrderByDescending(w =>
        w.TryGetProperty("completedAt", out var at) && DateTimeOffset.TryParse(at.ToString(), out var dt)
            ? dt : DateTimeOffset.MinValue).ToArray();
    // Compact workout overview including observed strength training methods.
    // No raw activity JSON or personal authentication details are printed.
    static string Value(System.Text.Json.JsonElement obj, string name)
        => obj.ValueKind == System.Text.Json.JsonValueKind.Object &&
           obj.TryGetProperty(name, out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null
            ? value.ToString() : "—";

    if (matchingTimes)
        Console.WriteLine("EGYM workout completion timestamps (raw response, NOT assumed UTC):");
    foreach (var workout in matchingTimes ? sorted : sorted.Take(12))
    {
        var exercises = workout.TryGetProperty("exercises", out var entries) &&
                        entries.ValueKind == System.Text.Json.JsonValueKind.Array
            ? entries.EnumerateArray().ToArray()
            : Array.Empty<System.Text.Json.JsonElement>();
        var methods = exercises.SelectMany(ex =>
                ex.TryGetProperty("sets", out var sets) && sets.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? sets.EnumerateArray().ToArray()
                    : Array.Empty<System.Text.Json.JsonElement>())
            .Select(set => Value(set, "trainingMethod"))
            .Where(method => method != "—" && method.Length > 0)
            .Distinct().ToArray();
        Console.WriteLine($"{Value(workout, "completedAt")}: {exercises.Length} exercises; methods={string.Join(", ", methods)}" +
            (matchingTimes ? $"; timezone={Value(workout, "timezone")}" : ""));
    }

    if (matchingTimes) return 0;
    if (args.Contains("--circuit-check", StringComparer.OrdinalIgnoreCase))
    {
        var expectedIds = new long[] { 333, 334, 581, 330, 335, 329, 331, 332 };
        foreach (var workout in sorted.Take(12))
        {
            var counts = CircuitJsonNormalizer.EgymCounts(workout);
            var missing = expectedIds.Where(id => counts.Count(x => x.EgymActivityId == id && x.SetCount == 2) != 1).ToArray();
            var unexpected = counts.Where(x => !expectedIds.Contains(x.EgymActivityId)).ToArray();
            Console.WriteLine($"  EGYM circuit {Value(workout, "completedAt")}: strengthMachines={counts.Count}; twoSetsEach={missing.Length == 0 && unexpected.Length == 0}; missingOrIncomplete={string.Join(",", missing)}; extra={unexpected.Length}");
        }
        return 0;
    }


    if (sorted.Length > 0 && sorted[0].TryGetProperty("exercises", out var latestExercises) &&
        latestExercises.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        Console.WriteLine("Latest training — compact exercise overview:");
        foreach (var exercise in latestExercises.EnumerateArray())
        {
            var activityId = exercise.TryGetProperty("activity", out var activity)
                ? Value(activity, "activityId") : "—";
            Console.WriteLine($"{Value(exercise, "label")} (id={activityId})");
            if (!exercise.TryGetProperty("sets", out var sets) ||
                sets.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            foreach (var (set, index) in sets.EnumerateArray().Select((item, index) => (item, index)))
                Console.WriteLine($"  Set {index + 1}: reps={Value(set, "numberOfReps")}, " +
                    $"weight={Value(set, "weight")}, method={Value(set, "trainingMethod")}, " +
                    $"side={Value(set, "sideMode")}");
        }
    }
    // The EGYM Smart Strength circuit may be recorded in strength measurements
    // rather than the general workouts endpoint. Report only response structure,
    // not personal training values or raw JSON.
    var strength = await client.GetStrengthMeasurementsAsync(start, today);
    Console.WriteLine($"Strength measurements response type: {strength.ValueKind}");
    if (strength.ValueKind == System.Text.Json.JsonValueKind.Array)
        Console.WriteLine($"Strength measurements: {strength.GetArrayLength()} entries");
    else if (strength.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        foreach (var property in strength.EnumerateObject())
        {
            var shape = property.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? $"Array ({property.Value.GetArrayLength()} items)"
                : property.Value.ValueKind.ToString();
            Console.WriteLine($"Strength field: {property.Name} = {shape}");
        }
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Read-only EGYM request failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
