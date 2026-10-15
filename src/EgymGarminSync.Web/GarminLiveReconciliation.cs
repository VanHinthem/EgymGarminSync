using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Genuine Garmin GET readback without journal or cache mutation. No PUT transport.</summary>
internal static class GarminLiveReconciliation
{
    internal sealed record Request(long GarminId, long JournalId, GarminReconciliation.Edit[] Edits);
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    internal sealed record Observation(string ActivityJson, string ExerciseSetsJson);
    internal delegate Task<Observation> ReadOnlyReader(long activityId);

    public static Task<IResult> ReadAsync(Request request, SyncDbContext db) =>
        EvaluateAsync(request, db, ReadFromGarminAsync);

    internal static async Task<IResult> EvaluateAsync(Request request, SyncDbContext db, ReadOnlyReader reader)
    {
        if (request.GarminId<=0 || request.JournalId<=0 || request.Edits is not {Length:>0 and <=72})
            return Results.BadRequest(new { error="GarminId, Uncertain journal ID and edits required." });
        var journal=await db.WriteJournal.AsNoTracking().SingleOrDefaultAsync(x=>
            x.Id==request.JournalId && x.GarminActivityId==request.GarminId && x.Status=="Uncertain");
        if (journal is null)
            return Results.Conflict(new { error="Matching Uncertain journal required." });
        var backup=await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(x=>
            x.Id==journal.FreshSnapshotId && x.GarminActivityId==request.GarminId && x.Kind=="WritePreparation");
        if (backup is null || Hash(backup.ActivityJson)!=backup.ActivitySha256 ||
            Hash(backup.ExerciseSetsJson)!=backup.SetsSha256 ||
            journal.OriginalSha256!=backup.SetsSha256)
            return Results.Conflict(new { error="Immutable recovery backup missing or damaged." });
        string candidate;
        try
        {
            using var activityDoc=JsonDocument.Parse(backup.ActivityJson);
            if(!activityDoc.RootElement.TryGetProperty("activityId",out var id) ||
                !id.TryGetInt64(out var actualId) || actualId!=request.GarminId)
                return Results.Conflict(new { error="Backup activity identity mismatch." });
            var edits=request.Edits.Select(x=>new SetFieldEdit(x.SetIndex,x.Field,x.Expected,x.Value)).ToArray();
            if(edits.All(x=>x.Expected==x.Value))
                return Results.BadRequest(new { error="No changed fields." });
            candidate=GarminSetFieldDraft.Apply(backup.ExerciseSetsJson,edits);
            var payload=GarminTrialPayloadNormalizer.Normalize(candidate,request.GarminId);
            GarminExerciseSetsWriteContract.Validate(payload,request.GarminId);
            if(Hash(candidate)!=journal.CandidateSha256 || Hash(payload)!=journal.ProposedPayloadSha256)
                return Results.Conflict(new { error="Edits do not match journaled candidate." });
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or ArgumentException)
        {
            return Results.Conflict(new { error="Recovery candidate validation failed." });
        }
        try
        {
            var observation=await reader(request.GarminId);
            var liveActivity=observation.ActivityJson;
            var liveSets=observation.ExerciseSetsJson;
            using var liveDoc=JsonDocument.Parse(liveActivity);
            var validIdentity=liveDoc.RootElement.TryGetProperty("activityId",out var liveId) &&
                liveId.TryGetInt64(out var number) && number==request.GarminId;
            var metadataOk=validIdentity &&
                GarminActivityMetadataTolerance.IsAcceptable(backup.ActivityJson,liveActivity);
            string decision;
            try {
                decision=metadataOk
                    ? GarminReconciliation.Classify(backup.ExerciseSetsJson,candidate,liveSets)
                    : "Uncertain";
            }
            catch(JsonException) { decision="Uncertain"; }
            return Results.Ok(new {
                readOnly=true, writeEnabled=false, garminReadOnly=true,
                activityId=request.GarminId, journalId=journal.Id,
                currentJournalStatus=journal.Status, decision, metadataOk,
                observedSetsSha256=Hash(liveSets), journalUnchanged=true,
                cacheUpdated=false, garminPutAttempted=false,
                wouldClearBlockAfterApproval=decision!="Uncertain",
                note="Live GET-only assessment. Uncertain journal remains blocked; no reconciliation persisted."
            });
        }
        catch(Exception)
        {
            return Results.Problem("Live Garmin readback failed; journal remains Uncertain, no write attempted.",statusCode:502);
        }
    }

    private static async Task<Observation> ReadFromGarminAsync(long activityId)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows DPAPI required.");
        var email=Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password=Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if(string.IsNullOrWhiteSpace(email)||string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Garmin credentials unavailable.");
        var tokenCache=new WindowsGarminTokenCache(email);
        if(tokenCache.IsInLoginCooldown(out _)) throw new InvalidOperationException("Garmin login cooldown.");
        using var http=new HttpClient(GarminIpv4Transport.Create());
        var context=new GarminConnectContext(http,new BasicAuthParameters(email,password),
            new NotImplementedMfaCode(),tokenCache);
        var client=new GarminConnectClient(context);
        var recent=await client.GetActivities(0,100);
        if(!recent.Any(x=>x.ActivityId==activityId &&
            x.ActivityName is "EGYM Positief" or "EGYM Negatief"))
            throw new InvalidOperationException("Target not a recent EGYM activity.");
        var activity=(await context.GetAndDeserialize<JsonElement>(
            $"/activity-service/activity/{activityId}")).GetRawText();
        var sets=(await context.GetAndDeserialize<JsonElement>(
            $"/activity-service/activity/{activityId}/exerciseSets")).GetRawText();
        tokenCache.ClearLoginCooldown();
        return new Observation(activity,sets);
    }
}
