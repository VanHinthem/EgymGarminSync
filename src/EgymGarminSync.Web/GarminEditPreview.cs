using System.Text.Json;
using EgymGarminSync.Matching;
using EgymGarminSync.Garmin.Cli;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;

namespace EgymGarminSync.Web;

internal static class GarminEditPreview
{
    internal sealed record Edit(int SetIndex, string Field, decimal Expected, decimal Value);
    internal sealed record Request(long GarminId, Edit[] Edits);

    public static async Task<IResult> PreviewAsync(Request request)
    {
        if (!OperatingSystem.IsWindows()) return Results.Problem("Windows DPAPI required.", statusCode:501);
        if (request.GarminId <= 0 || request.Edits is not { Length: > 0 and <= 72 })
            return Results.BadRequest(new { error="Select one or more Garmin field changes." });
        var email=Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password=Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email)||string.IsNullOrWhiteSpace(password))
            return Results.Problem("Garmin credentials unavailable.",statusCode:503);
        try
        {
            var cache=new WindowsGarminTokenCache(email);
            if (cache.IsInLoginCooldown(out _)) return Results.Problem("Garmin login cooldown.",statusCode:429);
            using var http=new HttpClient();
            var context=new GarminConnectContext(http,new BasicAuthParameters(email,password),
                new NotImplementedMfaCode(),cache);
            var client=new GarminConnectClient(context);
            var recent=await client.GetActivities(0,100);
            if (!recent.Any(a=>a.ActivityId==request.GarminId &&
                (a.ActivityName is "EGYM Positief" or "EGYM Negatief")))
                return Results.BadRequest(new {error="Garmin activity is not a recent EGYM workout."});
            var live=await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{request.GarminId}/exerciseSets");
            var edits=request.Edits.Select(x=>new SetFieldEdit(x.SetIndex,x.Field,x.Expected,x.Value)).ToArray();
            var original=live.GetRawText();
            var proposed=GarminSetFieldDraft.Apply(original,edits);
            cache.ClearLoginCooldown();
            return Results.Ok(new {
                readOnly=true, canWrite=false, activityId=request.GarminId,
                setCount=live.GetProperty("exerciseSets").GetArrayLength(),
                edits=edits.Select(x=>new {setIndex=x.SetIndex,field=x.Field,from=x.Expected,to=x.Value}).ToArray(),
                fieldChanges=edits.Count(x=>x.Expected!=x.Value),
                message="Preview validated against fresh Garmin GET; no PUT and no backup created."
            });
        }
        catch(InvalidOperationException)
        {
            return Results.Conflict(new {error="Current Garmin values differ or a proposed edit is unsafe. Reload the comparison."});
        }
        catch(Exception)
        {
            return Results.Problem("Could not validate Garmin draft; no write attempted.",statusCode:502);
        }
    }
}
