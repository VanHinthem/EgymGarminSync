using System.Text.Json;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
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
    Console.WriteLine("Step 1: listing Garmin workouts...");
    var workouts = await client.GetWorkouts(new WorkoutsParameters { Start = 0, Limit = 100 });
    Console.WriteLine($"Step 1 OK: {workouts.Length} workouts");
    Console.WriteLine("EGYM Garmin templates:");
    foreach (var id in new long[] { 1464321438, 1464319904 })
    {
        var listed = workouts.FirstOrDefault(w => w.WorkoutId == id);
        if (listed is null)
        {
            Console.WriteLine($"  Template {id}: not included in fetched workout list");
            continue;
        }
        Console.WriteLine($"  {listed.WorkoutId}: {listed.WorkoutName}");
        try
        {
            Console.WriteLine($"Step 2: fetching template {id}...");
            var full = await client.GetWorkout(id);
            PrintShape(JsonSerializer.SerializeToElement(full), "template", 0, 6);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Template {id} failed ({ex.GetType().Name}).");
        }
    }

    // Recorded exercise sets are distinct from template steps; both must be inspected.
    Console.WriteLine("Registered strength activity exercise sets:");
    foreach (var (id, label) in new[] { (24180037460L, "EGYM Negatief"), (23883291887L, "EGYM Positief") })
    {
        Console.WriteLine($"  {label} ({id}):");
        try
        {
            Console.WriteLine($"Step 3: fetching exercise sets for activity {id}...");
            var sets = await client.GetActivityExerciseSets(id);
            PrintShape(JsonSerializer.SerializeToElement(sets), "activity", 0, 6);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Activity {id} failed ({ex.GetType().Name}).");
        }
    }
    return 0;
}
catch (GarminConnectAuthenticationException ex)
{
    Console.Error.WriteLine($"Garmin authentication failed (code={ex.Code}, innerType={ex.InnerException?.GetType().Name ?? "none"}).");
    Console.Error.WriteLine("No response bodies, tokens or authentication messages are printed.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Garmin read-only discovery failed ({ex.GetType().Name}).");
    return 1;
}

// Print only a bounded structural schema and training-relevant scalar values;
// never dump auth, cookies, URLs, owner details or complete Garmin payloads.
static void PrintShape(JsonElement element, string path, int depth, int maxDepth)
{
    if (depth > maxDepth) return;
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("user", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("owner", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("author", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    PrintShape(prop.Value, path + "." + prop.Name, depth + 1, maxDepth);
                else if (new[] { "workoutId", "workoutName", "stepId", "stepOrder", "stepType", "exerciseName", "category", "exerciseCategory", "exerciseName", "weight", "weightValue", "repetitionCount", "numberOfReps", "durationValue", "durationType", "setType", "exerciseId" }
                         .Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"    {path}.{prop.Name} = {prop.Value}");
            }
            break;
        case JsonValueKind.Array:
            Console.WriteLine($"    {path}: {element.GetArrayLength()} items");
            foreach (var (item, index) in element.EnumerateArray().Take(2).Select((item, index) => (item, index)))
                PrintShape(item, $"{path}[{index}]", depth + 1, maxDepth);
            break;
    }
}
