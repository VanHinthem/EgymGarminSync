using GarminConnect;

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
    foreach (var workout in workouts)
        Console.WriteLine($"  {workout.WorkoutId}: {workout.WorkoutName}");

    var activities = await client.GetActivitiesAsync(0, 20);
    Console.WriteLine("Recent Garmin activities:");
    foreach (var activity in activities)
        Console.WriteLine($"  {activity.ActivityId}: {activity.ActivityName}");

    return 0;
}
catch (Exception ex)
{
    // Do not print full request/response bodies or authentication tokens.
    Console.Error.WriteLine($"Garmin read-only discovery failed ({ex.GetType().Name}).");
    return 1;
}
