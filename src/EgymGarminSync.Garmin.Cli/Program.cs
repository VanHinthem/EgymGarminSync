using GarminConnect;
using System.Text.Json;

// READ-ONLY Garmin discovery proof of concept.
// No Garmin mutation calls are used here.
var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
if (string.IsNullOrWhiteSpace(email))
{
    Console.Error.WriteLine("Set GARMIN_EMAIL before running.");
    return 1;
}

Console.Write("Garmin password (input visible; run in a private terminal): ");
var password = Console.ReadLine();
if (string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("No password supplied.");
    return 1;
}

try
{
    using var client = new GarminClient();
    var result = await client.LoginAsync(email, password);
    password = string.Empty;
    if (result.RequiresMfa)
    {
        Console.Write("Garmin MFA code: ");
        var code = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(code))
            return 1;
        result = await client.CompleteMfaAsync(code);
    }

    if (!result.Success)
    {
        Console.Error.WriteLine("Garmin authentication was not completed.");
        return 1;
    }

    var workouts = await client.GetWorkoutsAsync(0, 100);
    Console.WriteLine("Garmin workout templates:");
    if (workouts.ValueKind == JsonValueKind.Array)
        foreach (var workout in workouts.EnumerateArray())
            Console.WriteLine($"  {Field(workout, "workoutId")}: {Field(workout, "workoutName")}");
    else
        Console.WriteLine($"  Response shape: {workouts.ValueKind}");

    var activities = await client.GetActivitiesAsync(0, 20);
    Console.WriteLine("Recent Garmin activities:");
    foreach (var activity in activities)
    {
        var value = JsonSerializer.SerializeToElement(activity);
        Console.WriteLine($"  {Field(value, "activityId", "ActivityId")}: {Field(value, "activityName", "ActivityName")}");
    }

    return 0;
}
catch (Exception ex)
{
    // Do not print full request/response bodies or authentication tokens.
    Console.Error.WriteLine($"Garmin read-only discovery failed ({ex.GetType().Name}).");
    return 1;
}

static string Field(JsonElement obj, params string[] names)
{
    if (obj.ValueKind != JsonValueKind.Object) return "—";
    foreach (var name in names)
        if (obj.TryGetProperty(name, out var val) && val.ValueKind != JsonValueKind.Null)
            return val.ToString();
    return "—";
}
