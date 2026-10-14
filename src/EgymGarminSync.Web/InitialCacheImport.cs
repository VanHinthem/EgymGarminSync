using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Egym;
using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Initial server-side imports. Checkpoints mean completed imports, not API modification cursors.</summary>
internal static class InitialCacheImport
{
    public static async Task<IResult> ImportAsync(SyncDbContext db, bool incremental = false)
    {
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Garmin import currently requires Windows DPAPI.", statusCode: 501);
        var ee=Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var ep=Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        var ge=Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var gp=Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (new[] { ee,ep,ge,gp }.Any(string.IsNullOrWhiteSpace))
            return Results.Problem("Configure both API accounts in the server process.",statusCode:503);

        var previous = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Source == "EGYM");
        if (incremental && previous is null)
            return Results.Conflict(new { error = "Initial import required before rolling sync." });
        var since = incremental
            ? DateOnly.FromDateTime(previous!.LastSuccessfulUtc.UtcDateTime.AddDays(-7))
            : DateOnly.FromDateTime(DateTime.Today.AddDays(-180));
        // Do not advance either checkpoint until the complete source fetch succeeds.
        List<JsonElement> egymWorkouts;
        (long Id,string Name,DateTime Utc)[] garminActivities;
        var refreshedSets = new List<(long Id, string Raw, string Hash)>();
        try
        {
            using var egym=new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"),ee!,ep!);
            var today=DateOnly.FromDateTime(DateTime.Today);
            egymWorkouts=(await egym.GetWorkoutsAsync(since,today)).ToList();
            var cache=new WindowsGarminTokenCache(ge!);
            if(cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown.",statusCode:429);
            using var http = new HttpClient(GarminIpv4Transport.Create());
            var context=new GarminConnectContext(http,
                new BasicAuthParameters(ge!,gp!),new NotImplementedMfaCode(),cache);
            var client=new GarminConnectClient(context);
            garminActivities=(await client.GetActivities(0,100))
                .Where(a=>a.ActivityName is "EGYM Positief" or "EGYM Negatief")
                .Select(a=>(a.ActivityId,a.ActivityName,DateTime.SpecifyKind(a.StartTimeGmt,DateTimeKind.Utc)))
                .ToArray();
            if (incremental)
            {
                var cutoff = since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var recent = garminActivities.Where(a => a.Utc >= cutoff).ToArray();
                // Fetch before opening the transaction. Any failure aborts the entire
                // rolling sync; existing cache and checkpoints remain untouched.
                foreach (var activity in recent)
                {
                    var response = await context.GetAndDeserialize<JsonElement>(
                        $"/activity-service/activity/{activity.Id}/exerciseSets");
                    if (!response.TryGetProperty("exerciseSets", out var sets) ||
                        sets.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("Invalid Garmin exercise-set response.");
                    var raw = response.GetRawText();
                    refreshedSets.Add((activity.Id, raw, Hash(raw)));
                }
            }
            cache.ClearLoginCooldown();
        }
        catch(Exception)
        {
            return Results.Problem("Import failed to read EGYM or Garmin. Database not changed.",statusCode:502);
        }

        var now=DateTimeOffset.UtcNow;
        if (incremental)
        {
            var cutoff = since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            garminActivities = garminActivities.Where(a => a.Utc >= cutoff).ToArray();
        }
        var items=new List<CachedSession>();
        foreach(var workout in egymWorkouts)
        {
            var json=workout.GetRawText();
            var hash=Hash(json);
            var completed=CompletedUtc(workout);
            if (completed is null)
                return Results.Conflict(new { error="EGYM completion timestamp missing or ambiguous." });
            items.Add(new CachedSession {
                Source="EGYM", SourceId="completed:"+completed.Value.ToUnixTimeMilliseconds(),
                Sha256=hash, SourceJson=json,
                FetchedUtc=now, StartUtc=completed,
                ExerciseCount=workout.TryGetProperty("exercises",out var exercises)
                    && exercises.ValueKind==JsonValueKind.Array?exercises.GetArrayLength():0
            });
        }
        foreach(var activity in garminActivities)
        {
            var json=JsonSerializer.Serialize(new {activityId=activity.Id,name=activity.Name,
                startUtc=activity.Utc});
            items.Add(new CachedSession {
                Source="Garmin",SourceId=activity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name=activity.Name,StartUtc=new DateTimeOffset(activity.Utc),SourceJson=json,
                Sha256=Hash(json),FetchedUtc=now
            });
        }

        if (items.Where(x=>x.Source=="EGYM").GroupBy(x=>x.SourceId).Any(g=>g.Count()>1))
            return Results.Conflict(new { error="Duplicate EGYM completion timestamp; refusing to merge." });
        await using var transaction=await db.Database.BeginTransactionAsync();
        var legacy = await db.Sessions.Where(x=>x.Source=="EGYM" &&
            !x.SourceId.StartsWith("completed:")).ToListAsync();
        foreach(var old in legacy)
            if(items.Count(x=>x.Source=="EGYM" && x.StartUtc==old.StartUtc)==1)
                db.Sessions.Remove(old);
        foreach (var refreshed in refreshedSets)
        {
            var current = await db.GarminSets.FindAsync(refreshed.Id);
            if (current is null)
            {
                db.GarminSets.Add(new GarminSetsCache {
                    GarminActivityId = refreshed.Id, ExerciseSetsJson = refreshed.Raw,
                    Sha256 = refreshed.Hash, FetchedUtc = now
                });
                // The first observed response is not necessarily the historic original.
                var metadata = items.Single(x => x.Source == "Garmin" &&
                    x.SourceId == refreshed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!await db.GarminSnapshots.AnyAsync(x =>
                    x.GarminActivityId == refreshed.Id && x.Kind == "FirstObserved"))
                {
                    db.GarminSnapshots.Add(new GarminSnapshot {
                        GarminActivityId = refreshed.Id, CapturedUtc = now, Kind = "FirstObserved",
                        ActivityJson = metadata.SourceJson, ActivitySha256 = metadata.Sha256,
                        ExerciseSetsJson = refreshed.Raw, SetsSha256 = refreshed.Hash
                    });
                }
            }
            else if (current.Sha256 != refreshed.Hash)
            {
                current.ExerciseSetsJson = refreshed.Raw;
                current.Sha256 = refreshed.Hash;
                current.FetchedUtc = now;
            }
            else current.FetchedUtc = now;
        }
        foreach(var item in items)
        {
            var existing=await db.Sessions.FindAsync(item.Source,item.SourceId);
            if(existing is null) db.Sessions.Add(item);
            else
            {
                existing.SourceJson=item.SourceJson;
                existing.Sha256=item.Sha256;
                existing.FetchedUtc=now;
                existing.StartUtc=item.StartUtc;
                existing.Name=item.Name;
                existing.ExerciseCount=item.ExerciseCount;
            }
        }
        foreach(var (source,count) in new[]{("EGYM",egymWorkouts.Count),("Garmin",garminActivities.Length)})
        {
            var checkpoint=await db.Checkpoints.FindAsync(source);
            if(checkpoint is null)db.Checkpoints.Add(new SyncCheckpoint {
                Source=source,LastSuccessfulUtc=now,LastImportedCount=count
            });
            else
            {
                checkpoint.LastSuccessfulUtc=now;
                checkpoint.LastImportedCount=count;
                checkpoint.Error=null;
            }
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.Ok(new {egymImported=egymWorkouts.Count,garminImported=garminActivities.Length,
            lastSuccessfulUtc=now,mode=incremental?"rolling-refresh":"initial-full-import",
            garminSetsRefreshed=refreshedSets.Count,
            limitation="EGYM completion time is provisional identity. Garmin discovery covers at most 100 latest activities; older changes may require full import."});
    }

    private static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static DateTimeOffset? CompletedUtc(JsonElement workout)
    {
        try
        {
            var zone=TimeZoneInfo.FindSystemTimeZoneById(workout.GetProperty("timezone").GetString()!);
            var local=DateTime.SpecifyKind(DateTime.Parse(workout.GetProperty("completedAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture),DateTimeKind.Unspecified);
            if(zone.IsAmbiguousTime(local)||zone.IsInvalidTime(local))return null;
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,zone));
        }
        catch(Exception){return null;}
    }
}
