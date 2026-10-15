using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Safe journal-only startup recovery. Never reissues an interrupted write.</summary>
internal static class GarminCrashRecovery
{
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    internal static async Task<int> RecoverAsync(SyncDbContext db)
    {
        await using var tx=await db.Database.BeginTransactionAsync();
        var interrupted=await db.WriteJournal.Where(x=>x.Status=="WriteStarted").ToListAsync();
        foreach(var entry in interrupted)
        {
            // Missing/damaged payload is still Uncertain. Never guess a completed PUT.
            var payload=await db.WritePayloads.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.JournalId==entry.Id &&
                    x.GarminActivityId==entry.GarminActivityId);
            var intact=payload is not null &&
                Hash(payload.CandidateJson)==entry.CandidateSha256 &&
                Hash(payload.OutgoingJson)==entry.ProposedPayloadSha256 &&
                payload.CandidateSha256==entry.CandidateSha256 &&
                payload.OutgoingSha256==entry.ProposedPayloadSha256;
            entry.Status="Uncertain";
            entry.ErrorCode=intact ? "INTERRUPTED_WRITE_REQUIRES_RECONCILIATION" :
                "INTERRUPTED_WRITE_PAYLOAD_INVALID";
            entry.UpdatedUtc=DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return interrupted.Count;
    }
}
