using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Safe metadata-only inspection. No raw private activity JSON is returned.</summary>
internal static class GarminWriteJournalRead
{
    public static async Task<IResult> GetAsync(long activityId, SyncDbContext db)
    {
        if (activityId <= 0) return Results.BadRequest();
        var records = await db.WriteJournal.AsNoTracking()
            .Where(x => x.GarminActivityId == activityId)
            .OrderByDescending(x => x.Id)
            .Select(x => new {
                x.Id, x.GarminActivityId, x.ApprovedSnapshotId, x.FreshSnapshotId,
                x.Status, x.CreatedUtc, x.UpdatedUtc, x.ErrorCode,
                x.OriginalSha256, x.CandidateSha256, x.ProposedPayloadSha256,
                x.RecoveryPayloadSha256
            }).ToListAsync();
        return Results.Ok(new { activityId, writeEnabled = false, records });
    }
}
