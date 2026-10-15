using System.Security.Cryptography;
using System.Text;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Deterministic, side-effect-free simulation of a future write lifecycle.
/// Never calls Garmin, and never changes SQLite journal, snapshots or sets cache.
/// </summary>
internal static class GarminWriteSimulation
{
    internal sealed record Edit(int SetIndex, string Field, decimal Expected, decimal Value);
    internal sealed record Request(long GarminId, long SnapshotId, Edit[] Edits, string Scenario);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async Task<IResult> RunAsync(Request request, SyncDbContext db)
    {
        if (request.GarminId <= 0 || request.SnapshotId <= 0 ||
            request.Edits is not { Length: > 0 and <= 72 } ||
            request.Scenario is not ("Verified" or "Uncertain"))
            return Results.BadRequest(new { error = "Provide GarminId, SnapshotId, edits and scenario Verified or Uncertain." });

        var snapshot = await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.SnapshotId && x.GarminActivityId == request.GarminId &&
            x.Kind == "WritePreparation");
        if (snapshot is null || Hash(snapshot.ActivityJson) != snapshot.ActivitySha256 ||
            Hash(snapshot.ExerciseSetsJson) != snapshot.SetsSha256)
            return Results.Conflict(new { error = "Missing or damaged immutable WritePreparation snapshot." });

        if (await db.WriteJournal.AsNoTracking().AnyAsync(x => x.GarminActivityId == request.GarminId &&
            (x.Status == "Preparing" || x.Status == "Ready" ||
             x.Status == "WriteStarted" || x.Status == "Uncertain")))
            return Results.Conflict(new { error = "Unresolved write exists; simulation refused." });

        try
        {
            var edits = request.Edits.Select(e =>
                new SetFieldEdit(e.SetIndex,e.Field,e.Expected,e.Value)).ToArray();
            if (!edits.Any(e => e.Expected != e.Value))
                return Results.BadRequest(new { error = "No changed fields." });
            var candidate = GarminSetFieldDraft.Apply(snapshot.ExerciseSetsJson, edits);
            var normalized = GarminTrialPayloadNormalizer.Normalize(candidate,request.GarminId);
            var recovery = GarminTrialPayloadNormalizer.Normalize(snapshot.ExerciseSetsJson,request.GarminId);
            GarminExerciseSetsWriteContract.Validate(normalized,request.GarminId);
            GarminExerciseSetsWriteContract.Validate(recovery,request.GarminId);

            // The simulated response is deliberately explicit, not confused
            // with an actual HTTP response from Garmin.
            var simulatedReadback = request.Scenario == "Verified"
                ? candidate : snapshot.ExerciseSetsJson;
            var verified = GarminFullWeightReadback.Matches(candidate, simulatedReadback);
            var terminalStatus = verified ? "Verified" : "Uncertain";
            var statuses = new[] { "Ready", "WriteStarted", terminalStatus };
            return Results.Ok(new {
                simulated = true, readOnly = true, canWrite = false,
                garminHttpRequests = 0, sqliteWrites = 0, cacheUpdated = false,
                activityId = request.GarminId, freshBackupId = snapshot.Id,
                statuses, terminalStatus,
                writeAttempted = false, readbackVerified = verified,
                wouldUpdateCacheAfterRealReadback = verified,
                wouldBlockFurtherWrites = !verified,
                originalSha256 = snapshot.SetsSha256,
                candidateSha256 = Hash(candidate),
                payloadSha256 = Hash(normalized),
                recoverySha256 = Hash(recovery),
                note = "Lifecycle simulation only. No PUT, no database write and no cache mutation."
            });
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            return Results.Conflict(new { error = "Edits are incompatible with the immutable backup." });
        }
    }
}
