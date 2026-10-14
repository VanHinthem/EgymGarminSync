using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;

namespace EgymGarminSync.Web;

/// <summary>Windows-local read-only Garmin client using the same DPAPI token cache as CLI.</summary>
internal static class GarminReadOnlySessions
{
    public static async Task<IResult> GetAsync(ILoggerFactory loggerFactory)
    {
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Garmin authentication currently requires Windows DPAPI.", statusCode: 501);
        var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Results.Problem("Configure GARMIN_EMAIL and GARMIN_PASSWORD in the API process.", statusCode: 503);
        var logger = loggerFactory.CreateLogger("GarminReadOnlySessions");
        var stage = "token-cache";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var cache = new WindowsGarminTokenCache(email);
            if (cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown is active; try again later.", statusCode: 429);
            using var http = new HttpClient(GarminIpv4Transport.Create());
            var context = new GarminConnectContext(http,
                new BasicAuthParameters(email, password), new NotImplementedMfaCode(), cache);
            var client = new GarminConnectClient(context);
            stage = "activities";
            logger.LogInformation("Garmin read-only request started; stage={Stage}", stage);
            var activities = await client.GetActivities(0, 100);
            logger.LogInformation("Garmin read-only request succeeded; elapsedMs={Elapsed}", watch.ElapsedMilliseconds);
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
        catch (Exception ex)
        {
            // No exception.Message/ToString: upstream errors may contain private URLs or credentials.
            logger.LogWarning("Garmin read-only failed; stage={Stage}, exceptionType={ExceptionType}, elapsedMs={Elapsed}",
                stage, ex.GetType().Name, watch.ElapsedMilliseconds);
            return Results.Problem($"Garmin failed in {stage} ({ex.GetType().Name}); see safe server diagnostics.",
                statusCode: 502);
        }
    }
}
