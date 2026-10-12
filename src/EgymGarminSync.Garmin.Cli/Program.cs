using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Parameters;

var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("Set GARMIN_EMAIL and GARMIN_PASSWORD in the current shell.");
    return 1;
}

try
{
    using var http = new HttpClient();
    var client = new GarminConnectClient(new GarminConnectContext(http, new BasicAuthParameters(email, password)));
    // Read-only operations; login is handled by the Garmin Connect context.
    var workouts = await client.GetWorkouts(new WorkoutsParameters { Start = 0, Limit = 100 });
    Console.WriteLine("Garmin workout templates:");
    foreach (var workout in workouts)
        Console.WriteLine($"  {workout.WorkoutId}: {workout.WorkoutName}");

    var activities = await client.GetActivities(0, 20);
    Console.WriteLine("Recent Garmin activities:");
    foreach (var activity in activities)
        Console.WriteLine($"  {activity.ActivityId}: {activity.ActivityName}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Garmin read-only discovery failed ({ex.GetType().Name}).");
    return 1;
}
