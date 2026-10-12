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
    var days = 180;
    if (args.Length > 0 && (!int.TryParse(args[0], out days) || days is < 1 or > 730))
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
    foreach (var workout in sorted.Take(10))
    {
        var date = workout.TryGetProperty("completedAt", out var completed) ? completed.ToString() : "(unknown)";
        var count = workout.TryGetProperty("exercises", out var exercises) && exercises.ValueKind == System.Text.Json.JsonValueKind.Array
            ? exercises.GetArrayLength() : 0;
        Console.WriteLine($"{date}: {count} exercises");
    }
    // Display schema keys only. This lets us identify the data structure without
    // printing raw responses, authentication details, or personal training values.
    if (sorted.Length > 0)
    {
        var latest = sorted[0];
        Console.WriteLine("Latest workout fields: " + string.Join(", ", latest.EnumerateObject().Select(p => p.Name)));
        if (latest.TryGetProperty("exercises", out var exercises) &&
            exercises.ValueKind == System.Text.Json.JsonValueKind.Array && exercises.GetArrayLength() > 0)
        {
            var sample = exercises[0];
            Console.WriteLine("Exercise fields: " + string.Join(", ", sample.EnumerateObject().Select(p => p.Name)));
            foreach (var field in sample.EnumerateObject())
                if (field.Value.ValueKind == System.Text.Json.JsonValueKind.Array && field.Value.GetArrayLength() > 0 &&
                    field.Value[0].ValueKind == System.Text.Json.JsonValueKind.Object)
                    Console.WriteLine($"{field.Name} item fields: " + string.Join(", ", field.Value[0].EnumerateObject().Select(p => p.Name)));
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
