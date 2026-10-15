using System.Security.Cryptography;
using System.Text;
using EgymGarminSync.Matching;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Isolation-only finalization of a reviewed observation. Never reachable from an HTTP
/// endpoint; the observation must come from an independent, trusted live reader in future.
/// </summary>
internal static class GarminReconciliationFinalizer
{
    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static async Task<string> FinalizeIsolatedAsync(
        SyncDbContext db, long activityId, long journalId,
        string original, string candidate, string observed, string liveActivity)
    {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var entry=await db.WriteJournal.SingleOrDefaultAsync(x=>
            x.Id==journalId && x.GarminActivityId==activityId);
        if(entry is null || entry.Status!="Uncertain") return "Blocked";
        var backup=await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(x=>
            x.Id==entry.FreshSnapshotId && x.GarminActivityId==activityId &&
            x.Kind=="WritePreparation");
        if(backup is null || Hash(backup.ActivityJson)!=backup.ActivitySha256 ||
           Hash(backup.ExerciseSetsJson)!=backup.SetsSha256 ||
           entry.OriginalSha256!=backup.SetsSha256 ||
           !GarminFullWeightReadback.Matches(backup.ExerciseSetsJson,original) ||
           Hash(candidate)!=entry.CandidateSha256 ||
           !GarminActivityMetadataTolerance.IsAcceptable(backup.ActivityJson,liveActivity))
            return "Blocked";
        var decision=GarminReconciliation.Classify(original,candidate,observed);
        if(decision=="Uncertain") return "Uncertain";
        if(decision=="Verified")
        {
            var cache=await db.GarminSets.SingleOrDefaultAsync(x=>x.GarminActivityId==activityId);
            if(cache is null)
            {
                cache=new GarminSetsCache {GarminActivityId=activityId};
                db.GarminSets.Add(cache);
            }
            cache.ExerciseSetsJson=observed;
            cache.Sha256=Hash(observed);
            cache.FetchedUtc=DateTimeOffset.UtcNow;
        }
        entry.Status=decision;
        entry.UpdatedUtc=DateTimeOffset.UtcNow;
        entry.ErrorCode=null;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return decision;
    }
}
