using EgymGarminSync.Matching;
using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using Garmin.Connect.Parameters;

if (args.Contains("--offline-preflight-aug31", StringComparer.OrdinalIgnoreCase))
    return await OfflineSnapshotPreflight.RunAsync();

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Garmin CLI currently requires Windows for its DPAPI token cache.");
    return 1;
}

var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("Set GARMIN_EMAIL and GARMIN_PASSWORD in the current shell.");
    return 1;
}

WindowsGarminTokenCache? tokenCache = null;
try
{
    using var http = new HttpClient();
    tokenCache = new WindowsGarminTokenCache(email);
    if (tokenCache.IsInLoginCooldown(out var retryAfterUtc))
    {
        Console.Error.WriteLine($"Garmin login cooldown is active until {retryAfterUtc.ToLocalTime():yyyy-MM-dd HH:mm}. No login attempted.");
        return 1;
    }
    var context = new GarminConnectContext(http, new BasicAuthParameters(email, password), new NotImplementedMfaCode(), tokenCache);
    Console.WriteLine("Using Windows DPAPI encrypted Garmin OAuth token cache.");
    var client = new GarminConnectClient(context);
    if (args.Contains("--backup-aug31", StringComparer.OrdinalIgnoreCase))
    {
        var result = await GarminActivitySnapshot.RunAsync(context);
        if (result == 0) tokenCache.ClearLoginCooldown();
        return result;
    }
    if (args.Contains("--cycling-aug31", StringComparer.OrdinalIgnoreCase))
    {
        var result = await CyclingReadOnlyDiagnostic.RunAsync(context);
        if (result == 0) tokenCache.ClearLoginCooldown();
        return result;
    }
    if (args.Contains("--weight-preview-aug31", StringComparer.OrdinalIgnoreCase))
    {
        var result = await CombinedReadOnlyDiagnostic.RunAsync(context, client,
            new DateOnly(2026, 8, 31), 24180037460L, previewWeights: true);
        if (result == 0) tokenCache.ClearLoginCooldown();
        return result;
    }
    if (args.Contains("--history-compare", StringComparer.OrdinalIgnoreCase))
    {
        // Initially cap historical exercise-set GET requests. Optional second argument
        // allows a small first validation run before inspecting all matched sessions.
        var commandIndex = Array.FindIndex(args, arg => arg.Equals("--history-compare", StringComparison.OrdinalIgnoreCase));
        var max = 5;
        if (commandIndex + 1 < args.Length &&
            (!int.TryParse(args[commandIndex + 1], out max) || max is < 1 or > 30))
        {
            Console.Error.WriteLine("Use --history-compare [1..30] to limit session inspections.");
            return 1;
        }
        var result = await HistoricalComparisonDiagnostic.RunAsync(context, client, max);
        if (result == 0) tokenCache.ClearLoginCooldown();
        return result;
    }
    if (args.Contains("--history-match", StringComparer.OrdinalIgnoreCase))
    {
        var result = await HistoricalMatchingDiagnostic.RunAsync(client, 180);
        if (result == 0) tokenCache.ClearLoginCooldown();
        return result;
    }
    if (args.Contains("--compare-aug31", StringComparer.OrdinalIgnoreCase) ||
        args.Contains("--compare-aug07", StringComparer.OrdinalIgnoreCase))
    {
        var positive = args.Contains("--compare-aug07", StringComparer.OrdinalIgnoreCase);
        var exitCode = await CombinedReadOnlyDiagnostic.RunAsync(context, client,
            positive ? new DateOnly(2026, 8, 7) : new DateOnly(2026, 8, 31),
            positive ? 23883291887L : 24180037460L);
        if (exitCode == 0) tokenCache.ClearLoginCooldown();
        return exitCode;
    }
    if (args.Contains("--matching-times", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Read-only Garmin EGYM activity timestamps (GMT from Garmin):");
        var activities = await client.GetActivities(0, 100);
        tokenCache.ClearLoginCooldown();
        foreach (var activity in activities.Where(a => a.ActivityName is "EGYM Positief" or "EGYM Negatief"))
        {
            // Garmin's startTimeGMT is represented as DateTime without an offset.
            // Attach UTC explicitly, never implicitly convert using the computer timezone.
            var utcStart = DateTime.SpecifyKind(activity.StartTimeGmt, DateTimeKind.Utc);
            Console.WriteLine($"  {activity.ActivityId} | {activity.ActivityName} | startUtc={utcStart:O} | durationSeconds={activity.Duration:F0}");
        }
        return 0;
    }
    Console.WriteLine("Step 1: listing Garmin workouts...");
    var workouts = await client.GetWorkouts(new WorkoutsParameters { Start = 0, Limit = 100 });
    Console.WriteLine($"Step 1 OK: {workouts.Length} workouts");
    tokenCache.ClearLoginCooldown();
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
            var full = await context.GetAndDeserialize<JsonElement>($"/workout-service/workout/{id}");
            Console.WriteLine("  Raw template root keys: " + SafeKeys(full));
            if (full.TryGetProperty("workoutSegments", out var segments) && segments.ValueKind == JsonValueKind.Array)
                foreach (var segment in segments.EnumerateArray())
                    if (segment.TryGetProperty("workoutSteps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                        foreach (var step in steps.EnumerateArray())
                            if (step.TryGetProperty("type", out var type) && type.ToString() == "RepeatGroupDTO")
                                Console.WriteLine("  Repeat group keys: " + SafeKeys(step));
            PrintTemplate(full);
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
            var sets = await context.GetAndDeserialize<JsonElement>($"/activity-service/activity/{id}/exerciseSets");
            Console.WriteLine("  Exercise-set response root keys: " + SafeKeys(sets));
            PrintActivitySets(sets);
            if (args.Contains("--circuit-check", StringComparer.OrdinalIgnoreCase))
            {
                var countRecords = CircuitJsonNormalizer.GarminCounts(sets);
                var expectedIds = new long[] { 333, 334, 581, 330, 335, 329, 331, 332 };
                var expected = expectedIds.Select(id => ExerciseIdentityMapping.TryGetGarminKey(id, out var key) ? key : null).ToArray();
                var valid = expected.All(key => key is not null && countRecords.Count(x => x.Category == key.Category && x.ExerciseName == key.ExerciseName) == 2) && countRecords.Count == 16;
                Console.WriteLine($"    Garmin circuit completeness: 16 strength records expected; actual={countRecords.Count}; twoSetsEach={valid}");
            }
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
    if (ex.Code == Code.OAuth1TicketNotFound) tokenCache?.RecordLoginFailure();
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
                    prop.Name.Contains("author", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("image", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    PrintShape(prop.Value, path + "." + prop.Name, depth + 1, maxDepth);
                else if (new[] { "workoutId", "workoutName", "stepId", "stepOrder", "stepType", "exerciseName", "category", "exerciseCategory", "exerciseName", "weight", "weightValue", "repetitionCount", "numberOfReps", "durationValue", "durationType", "setType", "exerciseId", "type", "value", "unit", "targetType", "targetValue", "weightUnit", "repetitions", "exerciseNameId", "categoryId", "duration", "weightInGrams", "description", "childStepId", "numberOfIterations", "endConditionValue", "weight", "reps", "repCount", "startTime", "category", "name" }
                         .Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"    {path}.{prop.Name} = {prop.Value}");
            }
            break;
        case JsonValueKind.Array:
            Console.WriteLine($"    {path}: {element.GetArrayLength()} items");
            foreach (var (item, index) in element.EnumerateArray().Take(25).Select((item, index) => (item, index)))
                PrintShape(item, $"{path}[{index}]", depth + 1, maxDepth);
            break;
    }
}

static string SafeKeys(JsonElement value)
{
    if (value.ValueKind != JsonValueKind.Object) return $"({value.ValueKind})";
    return string.Join(", ", value.EnumerateObject().Select(p => p.Name).Where(name =>
        !new[] { "token", "user", "owner", "url", "author", "image", "cookie", "email" }
            .Any(blocked => name.Contains(blocked, StringComparison.OrdinalIgnoreCase))));
}

static string Field(JsonElement item, string key)
    => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) &&
       v.ValueKind != JsonValueKind.Null ? v.ToString() : "—";

static void PrintTemplate(JsonElement template)
{
    if (!template.TryGetProperty("workoutSegments", out var segments) || segments.ValueKind != JsonValueKind.Array) return;
    foreach (var segment in segments.EnumerateArray())
    {
        if (!segment.TryGetProperty("workoutSteps", out var steps) || steps.ValueKind != JsonValueKind.Array) continue;
        foreach (var step in steps.EnumerateArray())
        {
            if (Field(step, "type") != "RepeatGroupDTO")
            {
                Console.WriteLine($"    Top level: {Field(step, "type")} category={Field(step, "category")} order={Field(step, "stepOrder")}");
                continue;
            }
            Console.WriteLine($"    Repeat group: {Field(step, "numberOfIterations")} iterations");
            if (!step.TryGetProperty("workoutSteps", out var children) || children.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in children.EnumerateArray())
            {
                var category = Field(child, "category");
                if (category == "—") continue; // Separate rest steps remain untouched.
                Console.WriteLine($"      {category}/{Field(child, "exerciseName")} stepId={Field(child, "stepId")} reps={Field(child, "endConditionValue")} weight={Field(child, "weightValue")}");
            }
        }
    }
}

static void PrintActivitySets(JsonElement data)
{
    if (!data.TryGetProperty("exerciseSets", out var sets) || sets.ValueKind != JsonValueKind.Array)
    {
        Console.WriteLine("    No exerciseSets array found. Root keys: " + SafeKeys(data));
        return;
    }
    var records = sets.EnumerateArray().ToArray();
    Console.WriteLine($"    {records.Length} total set records; ACTIVE={records.Count(x => Field(x, "setType") == "ACTIVE")}; REST={records.Count(x => Field(x, "setType") == "REST")}");
    foreach (var (set, index) in records.Select((item, index) => (item, index)))
    {
        if (Field(set, "setType") != "ACTIVE") continue;
        var exercise = set.TryGetProperty("exercises", out var names) &&
                       names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0
            ? names[0] : default;
        Console.WriteLine($"      [{index}] {Field(exercise, "category")}/{Field(exercise, "name")} reps={Field(set, "repetitionCount")} weightRaw={Field(set, "weight")}");
    }
}
