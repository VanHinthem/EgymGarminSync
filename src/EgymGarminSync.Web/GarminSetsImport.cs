using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

internal static class GarminSetsImport
{
    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    public static async Task<IResult> GetCachedAsync(long activityId, SyncDbContext db)
    {
        var item = await db.GarminSets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GarminActivityId == activityId);
        if (item is null) return Results.NotFound();
        return Results.Ok(new { activityId, fetchedUtc = item.FetchedUtc,
            sha256 = item.Sha256, payload = JsonDocument.Parse(item.ExerciseSetsJson).RootElement.Clone() });
    }

    // page size is intentionally small; repeatedly invoke with skip=0,5,... to backfill.
    public static async Task<IResult> ImportAsync(SyncDbContext db, int skip = 0, int take = 5)
    {
        if (skip < 0 || take is < 1 or > 10)
            return Results.BadRequest(new { error = "Use non-negative skip and take 1..10." });
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Windows DPAPI is required.", statusCode: 501);
        var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Results.Problem("Garmin credentials unavailable.", statusCode: 503);
        try
        {
            var cache = new WindowsGarminTokenCache(email);
            if (cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown active.", statusCode: 429);
            using var http = new HttpClient(GarminIpv4Transport.Create());
            var context = new GarminConnectContext(http,
                new BasicAuthParameters(email, password), new NotImplementedMfaCode(), cache);
            var client = new GarminConnectClient(context);
            var activities = (await client.GetActivities(0, 100))
                .Where(a => a.ActivityName is "EGYM Positief" or "EGYM Negatief")
                .ToArray();
            var selection = activities.Skip(skip).Take(take).ToArray();
            var imported = new List<long>();
            foreach (var activity in selection)
            {
                var id = activity.ActivityId;
                var metadata = await db.Sessions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Source == "Garmin" &&
                        x.SourceId == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (metadata is null)
                    return Results.Conflict(new { error = "Garmin activity not present in local metadata cache. Run session import first." });

                var payload = await context.GetAndDeserialize<JsonElement>(
                    $"/activity-service/activity/{id}/exerciseSets");
                if (!payload.TryGetProperty("exerciseSets", out var sets) ||
                    sets.ValueKind != JsonValueKind.Array)
                    return Results.Problem("Garmin response has no exerciseSets array; nothing committed for this activity.", statusCode: 502);
                var raw = payload.GetRawText();
                var hash = Hash(raw);
                var now = DateTimeOffset.UtcNow;
                await using var transaction = await db.Database.BeginTransactionAsync();

                var current = await db.GarminSets.FindAsync(id);
                if (current is null)
                    db.GarminSets.Add(new GarminSetsCache {
                        GarminActivityId=id, ExerciseSetsJson=raw, Sha256=hash, FetchedUtc=now
                    });
                else
                {
                    current.ExerciseSetsJson=raw;
                    current.Sha256=hash;
                    current.FetchedUtc=now;
                }

                // FirstObserved means first captured NOW, not necessarily original Garmin
                // data: activities modified before import (e.g. Aug 31) remain modified.
                if (!await db.GarminSnapshots.AnyAsync(x =>
                    x.GarminActivityId == id && x.Kind == "FirstObserved"))
                {
                    db.GarminSnapshots.Add(new GarminSnapshot {
                        GarminActivityId=id, CapturedUtc=now, Kind="FirstObserved",
                        ActivityJson=metadata.SourceJson, ActivitySha256=metadata.Sha256,
                        ExerciseSetsJson=raw, SetsSha256=hash
                    });
                }
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                imported.Add(id);
            }
            cache.ClearLoginCooldown();
            return Results.Ok(new { imported=imported.Count, activityIds=imported,
                totalDiscovered=activities.Length, skip, take,
                firstObservedSnapshots=true, writeEnabled=false });
        }
        catch (Exception)
        {
            return Results.Problem("Garmin sets import failed. Previously completed activities remain cached; no Garmin write attempted.", statusCode: 502);
        }
    }
}
