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
    var workouts = await client.GetWorkoutsAsync(today.AddDays(-30), today);
    Console.WriteLine($"Retrieved {workouts.Count} workouts in the past 30 days.");
    // Only summary metadata; do not dump sensitive raw workout payloads to the terminal.
    foreach (var workout in workouts)
    {
        var date = workout.TryGetProperty("completedAt", out var completed) ? completed.ToString() : "(unknown)";
        var count = workout.TryGetProperty("exercises", out var exercises) && exercises.ValueKind == System.Text.Json.JsonValueKind.Array
            ? exercises.GetArrayLength() : 0;
        Console.WriteLine($"{date}: {count} exercises");
    }
    // The EGYM Smart Strength circuit may be recorded in strength measurements
    // rather than the general workouts endpoint. Report only response structure,
    // not personal training values or raw JSON.
    var strength = await client.GetStrengthMeasurementsAsync(today.AddDays(-30), today);
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
