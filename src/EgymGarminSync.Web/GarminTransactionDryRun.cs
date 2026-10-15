using System.Security.Cryptography;
using System.Text;
using EgymGarminSync.Matching;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Internal-only write orchestration rehearsal. All IO is provided by injected
/// synthetic delegates; no Garmin write transport is referenced or exposed.
/// </summary>
internal static class GarminTransactionDryRun
{
    internal sealed record Observation(string ActivityJson, string ExerciseSetsJson);
    internal delegate Task<Observation> Read(long activityId);
    internal delegate Task Write(long activityId, string proposedPayload);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async Task<string> ExecuteAsync(SyncDbContext db, long activityId,
        long journalId, Read read, Write write)
    {
        var journal = await db.WriteJournal.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == journalId && x.GarminActivityId == activityId && x.Status == "Ready");
        if (journal is null) return "Blocked";
        var backup = await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == journal.FreshSnapshotId && x.GarminActivityId == activityId &&
            x.Kind == "WritePreparation");
        if (backup is null || Hash(backup.ActivityJson) != backup.ActivitySha256 ||
            Hash(backup.ExerciseSetsJson) != backup.SetsSha256 ||
            journal.OriginalSha256 != backup.SetsSha256)
            return "Blocked";

        // Payload is supplied only by this controlled test: no client-supplied
        // edit or JSON can authorize real Garmin writes.
        var proposed = await read(activityId);
        if (!GarminActivityMetadataTolerance.IsAcceptable(backup.ActivityJson,proposed.ActivityJson) ||
            !GarminFullWeightReadback.Matches(backup.ExerciseSetsJson,proposed.ExerciseSetsJson))
            return "Stale";
        var payload = await db.WritePayloads.AsNoTracking().SingleOrDefaultAsync(x=>x.JournalId==journalId && x.GarminActivityId==activityId);
        if (payload is null || Hash(payload.CandidateJson) != journal.CandidateSha256 ||
            Hash(payload.OutgoingJson) != journal.ProposedPayloadSha256 ||
            payload.CandidateSha256 != journal.CandidateSha256 ||
            payload.OutgoingSha256 != journal.ProposedPayloadSha256)
            return "Blocked";

        // Persist intent *before* invoking the injected writer. Once this commit
        // succeeds, any exception or process termination requires reconciliation.
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var tracked = await db.WriteJournal.SingleAsync(x => x.Id == journalId);
            if (tracked.Status != "Ready") return "Blocked";
            tracked.Status = "WriteStarted";
            tracked.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        string result = "Uncertain";
        Observation? after = null;
        try
        {
            await write(activityId, payload.OutgoingJson);
            after = await read(activityId);
            if (GarminActivityMetadataTolerance.IsAcceptable(
                    backup.ActivityJson, after.ActivityJson) &&
                GarminFullWeightReadback.Matches(payload.CandidateJson, after.ExerciseSetsJson))
                result = "SimulatedVerified";
        }
        catch(Exception) { /* Unknown outcome; never retry the writer. */ }

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var tracked = await db.WriteJournal.SingleAsync(x => x.Id == journalId);
            if (tracked.Status != "WriteStarted") return "Uncertain";
            tracked.Status = result;
            tracked.ErrorCode = result == "Uncertain" ? "SYNTHETIC_WRITE_UNKNOWN" : null;
            tracked.UpdatedUtc = DateTimeOffset.UtcNow;
            // No cache update in a simulation, even after a matching readback.
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return result;
    }

    // In the isolated harness this is a durable record, never a Garmin authorization.
    internal static async Task<bool> PersistSyntheticPayloadAsync(
        SyncDbContext db, long activityId, long journalId, string candidate, string outgoing)
    {
        await using var tx=await db.Database.BeginTransactionAsync();
        var journal=await db.WriteJournal.AsNoTracking().SingleOrDefaultAsync(x=>
            x.Id==journalId && x.GarminActivityId==activityId && x.Status=="DryRunVerified");
        if(journal is null || Hash(candidate)!=journal.CandidateSha256 ||
            Hash(outgoing)!=journal.ProposedPayloadSha256 ||
            await db.WritePayloads.AnyAsync(x=>x.JournalId==journalId)) return false;
        db.WritePayloads.Add(new GarminWritePayload {
            JournalId=journalId,GarminActivityId=activityId,
            CandidateJson=candidate,OutgoingJson=outgoing,
            CandidateSha256=Hash(candidate),OutgoingSha256=Hash(outgoing)
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }
}
