using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// HTTP boundary for one explicitly approved Garmin write.
/// Approval is bound to activity, journal and exact payload hashes.
/// </summary>
internal static class GarminLiveWriteEndpoint
{
    // This activity was already corrected during development and is intentionally
    // protected from being modified again.
    private const long ProtectedActivityId = 24180037460;

    internal sealed record Request(
        long ActivityId,
        long JournalId,
        string CandidateSha256,
        string OutgoingSha256,
        string Confirmation);


    private static bool IsAuthorizedSingleChange(string originalJson,string candidateJson)
    {
        try
        {
            var original=JsonNode.Parse(originalJson)!;
            var sets=original["exerciseSets"]!.AsArray();
            JsonNode? firstBench=null;
            foreach(var set in sets)
            {
                if(set?["setType"]?.GetValue<string>()!="ACTIVE") continue;
                var exercises=set["exercises"]?.AsArray();
                if(exercises is null || exercises.Count==0) continue;
                if(exercises[0]?["category"]?.GetValue<string>()=="BENCH_PRESS" &&
                    exercises[0]?["name"] is null)
                {
                    firstBench=set;
                    break;
                }
            }
            if(firstBench is null || firstBench["weight"] is null ||
                firstBench["weight"]!.GetValue<decimal>()==21000m)
                return false;
            firstBench["weight"]=21000m;
            return JsonNode.DeepEquals(original,JsonNode.Parse(candidateJson));
        }
        catch { return false; }
    }

    internal static async Task<IResult> ExecuteAsync(Request request, SyncDbContext db)
    {
        if(!GarminWriteTransportAdapter.LiveWriteEnabled)
            return Results.Problem(
                "Live Garmin PUT is still disabled in source code.",
                statusCode: 503);

        if(request.ActivityId==ProtectedActivityId || request.ActivityId!=GarminWriteTransportAdapter.TrialActivityId)
            return Results.Conflict(new {
                error="This previously corrected Garmin activity is protected from further writes."
            });


        // Permission is for exactly the first recorded ACTIVE BENCH_PRESS weight
        // becoming 21kg. Rebuild that one-field modification from the immutable
        // pre-write backup, then compare the entire candidate JSON.
        var journal=await db.WriteJournal.AsNoTracking().SingleOrDefaultAsync(j=>
            j.Id==request.JournalId && j.GarminActivityId==request.ActivityId && j.Status=="Ready");
        var backup=journal is null ? null : await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(s=>
            s.Id==journal.FreshSnapshotId && s.GarminActivityId==request.ActivityId &&
            s.Kind=="WritePreparation");
        var prepared=await db.WritePayloads.AsNoTracking().SingleOrDefaultAsync(p=>
            p.JournalId==request.JournalId && p.GarminActivityId==request.ActivityId);
        if(backup is null || prepared is null || !IsAuthorizedSingleChange(
            backup.ExerciseSetsJson,prepared.CandidateJson))
            return Results.Conflict(new { error="Prepared payload is not exactly Bench Press set 1 to 21 kg." });

        var approved=await GarminWriteApproval.ApproveAsync(db,
            new GarminWriteApproval.Request(
                request.ActivityId,
                request.JournalId,
                request.CandidateSha256,
                request.OutgoingSha256,
                request.Confirmation));

        if(!approved)
            return Results.Conflict(new {
                error="Exact journal/payload approval was rejected."
            });

        var result=await GarminUnifiedWriteWorkflow.ExecuteLiveAsync(
            db,request.ActivityId,request.JournalId);

        return result switch
        {
            "Verified" => Results.Ok(new {
                activityId=request.ActivityId,
                journalId=request.JournalId,
                status=result,
                readbackVerified=true,
                cacheUpdated=true
            }),
            "Uncertain" => Results.Json(new {
                activityId=request.ActivityId,
                journalId=request.JournalId,
                status=result,
                readbackVerified=false,
                cacheUpdated=false,
                error="Garmin outcome is uncertain. No automatic retry is permitted."
            },statusCode:502),
            "Disabled" => Results.Problem(
                "Live Garmin PUT is disabled.",statusCode:503),
            _ => Results.Conflict(new {
                error="Garmin write was blocked before a verified completion."
            })
        };
    }
}
