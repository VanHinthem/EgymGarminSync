using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// One-time explicit approval for a precisely identified, already persisted
/// journal/payload. Never grants live Garmin network writes by itself.
/// </summary>
internal static class GarminWriteApproval
{
    internal sealed record Request(long ActivityId,long JournalId,string CandidateSha256,
        string OutgoingSha256,string Confirmation);

    internal static async Task<bool> ApproveAsync(SyncDbContext db,Request request)
    {
        if(request.ActivityId<=0 || request.JournalId<=0 ||
           request.Confirmation!=$"APPROVE GARMIN {request.ActivityId} {request.JournalId}")
            return false;
        await using var tx=await db.Database.BeginTransactionAsync();
        var journal=await db.WriteJournal.SingleOrDefaultAsync(j=>
            j.Id==request.JournalId && j.GarminActivityId==request.ActivityId &&
            j.Status=="Ready" && j.OperationId==null);
        var payload=await db.WritePayloads.AsNoTracking().SingleOrDefaultAsync(p=>
            p.JournalId==request.JournalId && p.GarminActivityId==request.ActivityId);
        if(journal is null || payload is null ||
           !string.Equals(journal.CandidateSha256,request.CandidateSha256,StringComparison.Ordinal) ||
           !string.Equals(journal.ProposedPayloadSha256,request.OutgoingSha256,StringComparison.Ordinal) ||
           !string.Equals(payload.CandidateSha256,request.CandidateSha256,StringComparison.Ordinal) ||
           !string.Equals(payload.OutgoingSha256,request.OutgoingSha256,StringComparison.Ordinal))
            return false;
        // Approval is scoped to the currently committed immutable candidate and
        // expires rapidly. No approval route is available from the application.
        journal.ApprovedUntilUtc=DateTimeOffset.UtcNow.AddMinutes(10);
        journal.ApprovedCandidateSha256=request.CandidateSha256;
        journal.ApprovedOutgoingSha256=request.OutgoingSha256;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    internal static bool IsValid(GarminWriteJournal journal,GarminWritePayload payload,
        DateTimeOffset now) =>
        journal.ApprovedUntilUtc is { } until && until>now &&
        string.Equals(journal.ApprovedCandidateSha256,payload.CandidateSha256,StringComparison.Ordinal) &&
        string.Equals(journal.ApprovedOutgoingSha256,payload.OutgoingSha256,StringComparison.Ordinal) &&
        string.Equals(journal.CandidateSha256,payload.CandidateSha256,StringComparison.Ordinal) &&
        string.Equals(journal.ProposedPayloadSha256,payload.OutgoingSha256,StringComparison.Ordinal);
}
