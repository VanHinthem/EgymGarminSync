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
    public static async Task<IResult> ImportAsync(SyncDbContext db)
    {
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Garmin import currently requires Windows DPAPI.", statusCode: 501);
        var ee=Environment.GetEnvironmentVariable("EGYM_EMAIL");
        var ep=Environment.GetEnvironmentVariable("EGYM_PASSWORD");
        var ge=Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var gp=Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (new[] { ee,ep,ge,gp }.Any(string.IsNullOrWhiteSpace))
            return Results.Problem("Configure both API accounts in the server process.",statusCode:503);

        // Do not advance either checkpoint until the complete source fetch succeeds.
        List<JsonElement> egymWorkouts;
        (long Id,string Name,DateTime Utc)[] garminActivities;
        try
        {
            using var egym=new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"),ee!,ep!);
            var today=DateOnly.FromDateTime(DateTime.Today);
            egymWorkouts=(await egym.GetWorkoutsAsync(today.AddDays(-180),today)).ToList();
            var cache=new WindowsGarminTokenCache(ge!);
            if(cache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown.",statusCode:429);
            using var http=new HttpClient();
            var context=new GarminConnectContext(http,
                new BasicAuthParameters(ge!,gp!),new NotImplementedMfaCode(),cache);
            var client=new GarminConnectClient(context);
            garminActivities=(await client.GetActivities(0,100))
                .Where(a=>a.ActivityName is "EGYM Positief" or "EGYM Negatief")
                .Select(a=>(a.ActivityId,a.ActivityName,DateTime.SpecifyKind(a.StartTimeGmt,DateTimeKind.Utc)))
                .ToArray();
            cache.ClearLoginCooldown();
        }
        catch(Exception)
        {
            return Results.Problem("Import failed to read EGYM or Garmin. Database not changed.",statusCode:502);
        }

        var now=DateTimeOffset.UtcNow;
        var items=new List<CachedSession>();
        foreach(var workout in egymWorkouts)
        {
            var json=workout.GetRawText();
            var hash=Hash(json);
            // Until an upstream immutable workout identifier is verified, use a content
            // identity. This is deliberately not presented as reliable incremental upsert.
            items.Add(new CachedSession {
                Source="EGYM", SourceId=hash, Sha256=hash, SourceJson=json,
                FetchedUtc=now, StartUtc=CompletedUtc(workout),
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

        await using var transaction=await db.Database.BeginTransactionAsync();
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
            lastSuccessfulUtc=now,mode="initial-full-import",
            limitation="EGYM identities are content hashes; Garmin listing covers 100 latest activities. Incremental sync is not enabled."});
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
