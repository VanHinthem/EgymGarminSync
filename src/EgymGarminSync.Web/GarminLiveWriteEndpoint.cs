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

    internal static async Task<IResult> ExecuteAsync(Request request, SyncDbContext db)
    {
        if(!GarminWriteTransportAdapter.LiveWriteEnabled)
            return Results.Problem(
                "Live Garmin PUT requires GARMIN_WRITE_ENABLED=1 on this local Windows backend.",
                statusCode: 503);

        if(request.ActivityId==ProtectedActivityId)
            return Results.Conflict(new {
                error="This previously corrected Garmin activity is protected from further writes."
            });


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
