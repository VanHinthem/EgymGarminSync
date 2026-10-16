using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;

namespace EgymGarminSync.Web;

/// <summary>Read-only local investigation of the shape of generic Garmin strength activities.</summary>
internal static class GarminGenericStrengthInspection
{
    internal static async Task<IResult> InspectAsync(long activityId, HttpContext requestContext)
    {
        // Diagnostic endpoint intentionally limited to the local developer machine.
        if (requestContext.Connection.RemoteIpAddress is not { } address ||
            !System.Net.IPAddress.IsLoopback(address))
            return Results.StatusCode(403);
        if (activityId <= 0) return Results.BadRequest(new { error = "Invalid Garmin activity ID." });
        if (!GarminTokenCacheProvider.IsSupported)
            return Results.Problem("Garmin token cache unavailable.", statusCode: 501);
        var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Results.Problem("Garmin credentials are not configured.", statusCode: 503);
        try
        {
            var cache = new GarminTokenCacheProvider(email);
            if (cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown.", statusCode: 429);
            using var http = new HttpClient(GarminIpv4Transport.Create());
            var context = new GarminConnectContext(http,
                new BasicAuthParameters(email, password), new NotImplementedMfaCode(), cache);
            var client = new GarminConnectClient(context);
            var recent = await client.GetActivities(0, 100);
            if (!recent.Any(x => x.ActivityId == activityId))
                return Results.NotFound(new { error = "Activity is not among the 100 most recent Garmin activities." });

            // Fetch raw resources read-only. Return ONLY structural metadata, never workout payloads.
            var activity = await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{activityId}");
            var setsResponse = await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{activityId}/exerciseSets");
            var type = activity.TryGetProperty("activityType", out var typeNode) &&
                typeNode.ValueKind == JsonValueKind.Object &&
                typeNode.TryGetProperty("typeKey", out var key) &&
                key.ValueKind == JsonValueKind.String ? key.GetString() : null;
            if (type != "strength_training")
                return Results.BadRequest(new { error = "Activity is not a generic Garmin strength-training activity.", detectedType = type });
            if (!setsResponse.TryGetProperty("exerciseSets", out var sets) ||
                sets.ValueKind != JsonValueKind.Array)
                return Results.Problem("Garmin returned no exerciseSets array.", statusCode: 502);
            var entries = sets.EnumerateArray().ToArray();
            return Results.Ok(new {
                activityId,
                activityType = type,
                activityRootKeys = activity.EnumerateObject().Select(p => p.Name).Order().ToArray(),
                setsRootKeys = setsResponse.EnumerateObject().Select(p => p.Name).Order().ToArray(),
                setCount = entries.Length,
                activeCount = entries.Count(s => s.TryGetProperty("setType", out var v) && v.GetString() == "ACTIVE"),
                restCount = entries.Count(s => s.TryGetProperty("setType", out var v) && v.GetString() == "REST"),
                setFieldNames = entries.SelectMany(s => s.ValueKind == JsonValueKind.Object
                    ? s.EnumerateObject().Select(p => p.Name) : Array.Empty<string>()).Distinct().Order().ToArray(),
                hasStartTimes = entries.Count(s => s.TryGetProperty("startTime", out var t) && t.ValueKind == JsonValueKind.String),
                nestedExerciseCount = entries.Sum(s => s.TryGetProperty("exercises", out var e) &&
                    e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0),
                readOnly = true
            });
        }
        catch (Exception)
        {
            return Results.Problem("Read-only Garmin structure inspection failed; no write attempted.", statusCode: 502);
        }
    }
}
