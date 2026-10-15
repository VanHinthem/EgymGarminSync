using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    internal static Task<string> ExecuteAsync(SyncDbContext db, long activityId,
        long journalId, Read read, Write write) =>
        ExecuteCoreAsync(db,activityId,journalId,read,write,false);

    // Isolated harness only: exercises the same terminal journal/cache transaction
    // expected from a future production implementation. Injected IO is synthetic.
    internal static Task<string> ExecuteVerifiedIsolationAsync(SyncDbContext db, long activityId,
        long journalId, Read read, Write write) =>
        ExecuteCoreAsync(db,activityId,journalId,read,write,true);

    private static async Task<string> ExecuteCoreAsync(SyncDbContext db, long activityId,
        long journalId, Read read, Write write, bool isolatedVerified)
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

        // A matching journal hash alone is not sufficient authorization: validate
        // full replace-all shape and derive the exact normalized payload anew.
        try
        {
            GarminExerciseSetsWriteContract.Validate(payload.OutgoingJson,activityId);
            var normalized=GarminTrialPayloadNormalizer.Normalize(payload.CandidateJson,activityId);
            if(!string.Equals(normalized,payload.OutgoingJson,StringComparison.Ordinal))
                return "Blocked";
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or ArgumentException)
        {
            return "Blocked";
        }

        var operationId=Guid.NewGuid().ToString("N");

        // Persist intent *before* invoking the injected writer. Once this commit
        // succeeds, any exception or process termination requires reconciliation.
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var tracked = await db.WriteJournal.SingleAsync(x => x.Id == journalId);
            if (tracked.Status != "Ready") return "Blocked";
            tracked.OperationId=operationId;
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
                result = isolatedVerified ? "Verified" : "SimulatedVerified";
        }
        catch(Exception) { /* Unknown outcome; never retry the writer. */ }

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var tracked = await db.WriteJournal.SingleAsync(x => x.Id == journalId);
            if (tracked.Status != "WriteStarted" || tracked.OperationId != operationId)
                return "Uncertain";
            tracked.Status = result;
            tracked.ErrorCode = result == "Uncertain" ? "SYNTHETIC_WRITE_UNKNOWN" : null;
            tracked.UpdatedUtc = DateTimeOffset.UtcNow;
            if(isolatedVerified && result=="Verified" && after is not null)
            {
                var cache=await db.GarminSets.SingleOrDefaultAsync(x=>x.GarminActivityId==activityId);
                if(cache is null)
                {
                    cache=new GarminSetsCache {GarminActivityId=activityId};
                    db.GarminSets.Add(cache);
                }
                cache.ExerciseSetsJson=after.ExerciseSetsJson;
                cache.Sha256=Hash(after.ExerciseSetsJson);
                cache.FetchedUtc=DateTimeOffset.UtcNow;
            }
            // Journal and optional synthetic cache update commit atomically.
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
