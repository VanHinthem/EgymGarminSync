using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;

namespace EgymGarminSync.Web;

/// <summary>Windows-local read-only Garmin client using the same DPAPI token cache as CLI.</summary>
internal static class GarminReadOnlySessions
{
    public static async Task<IResult> GetAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Garmin authentication currently requires Windows DPAPI.", statusCode: 501);
        var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Results.Problem("Configure GARMIN_EMAIL and GARMIN_PASSWORD in the API process.", statusCode: 503);
        try
        {
            var cache = new WindowsGarminTokenCache(email);
            if (cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown is active; try again later.", statusCode: 429);
            using var http = new HttpClient();
            var context = new GarminConnectContext(http,
                new BasicAuthParameters(email, password), new NotImplementedMfaCode(), cache);
            var client = new GarminConnectClient(context);
            var activities = await client.GetActivities(0, 100);
            var sessions = activities
                .Where(a => a.ActivityName is "EGYM Positief" or "EGYM Negatief")
                .Select(a => new
                {
                    id = a.ActivityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    completedAt = DateTime.SpecifyKind(a.StartTimeGmt, DateTimeKind.Utc).ToString("O"),
                    exerciseCount = 0,
                    name = a.ActivityName
                })
                .ToArray();
            cache.ClearLoginCooldown();
            return Results.Ok(sessions);
        }
        catch (Exception)
        {
            return Results.Problem("Could not read Garmin activities. Check local Garmin authentication.", statusCode: 502);
        }
    }
}
