using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Matching;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Offline rehearsal against an immutable snapshot. Never contacts Garmin.</summary>
internal static class GarminWriteRehearsal
{
    internal sealed record Edit(int SetIndex, string Field, decimal Expected, decimal Value);
    internal sealed record Request(long SnapshotId, Edit[] Edits);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async Task<IResult> RehearseAsync(Request request, SyncDbContext db)
    {
        if (request.SnapshotId <= 0 || request.Edits is not { Length: > 0 and <= 72 })
            return Results.BadRequest(new { error = "Select a snapshot and 1..72 edits." });
        var snapshot = await db.GarminSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.SnapshotId);
        if (snapshot is null || snapshot.Kind is not ("Preflight" or "FullPreflight"))
            return Results.NotFound(new { error = "Valid Preflight snapshot not found." });

        if (Hash(snapshot.ExerciseSetsJson) != snapshot.SetsSha256 ||
            Hash(snapshot.ActivityJson) != snapshot.ActivitySha256)
            return Results.Conflict(new { error = "Snapshot checksum validation failed." });
        try
        {
            var edits = request.Edits.Select(x =>
                new SetFieldEdit(x.SetIndex, x.Field, x.Expected, x.Value)).ToArray();
            var proposed = GarminSetFieldDraft.Apply(snapshot.ExerciseSetsJson, edits);
            using var originalDoc = JsonDocument.Parse(snapshot.ExerciseSetsJson);
            using var proposedDoc = JsonDocument.Parse(proposed);
            var oldSets = originalDoc.RootElement.GetProperty("exerciseSets");
            var newSets = proposedDoc.RootElement.GetProperty("exerciseSets");
            if (oldSets.GetArrayLength() != newSets.GetArrayLength())
                throw new InvalidOperationException("Set count changed.");
            // Apply already verifies that reversing all edited fields yields the
            // original complete JSON tree; the immutable snapshot is the recovery source.
            // Match the full replace-all format used by the previously verified
            // weight correction. This is still strictly offline.
            var normalized = GarminTrialPayloadNormalizer.Normalize(proposed, snapshot.GarminActivityId);
            GarminExerciseSetsWriteContract.Validate(normalized, snapshot.GarminActivityId);
            // Independently reconstruct a recovery payload from the immutable snapshot.
            var recoveryPayload = GarminTrialPayloadNormalizer.Normalize(
                snapshot.ExerciseSetsJson, snapshot.GarminActivityId);
            GarminExerciseSetsWriteContract.Validate(recoveryPayload, snapshot.GarminActivityId);
            var changed = edits.Count(x => x.Expected != x.Value);
            if (changed == 0)
                return Results.BadRequest(new { error = "No actual field changes." });
            return Results.Ok(new {
                readOnly = true, canWrite = false, canRestore = false,
                snapshotId = snapshot.Id, activityId = snapshot.GarminActivityId,
                integrityOk = true, setCount = oldSets.GetArrayLength(),
                replaceAllContractValid = true, normalizedPayloadSha256 = Hash(normalized),
                fieldChanges = changed,
                originalSha256 = snapshot.SetsSha256,
                proposedSha256 = Hash(proposed),
                recoverySourceSha256 = snapshot.SetsSha256,
                restorePayloadContractValid = true,
                restorePayloadSha256 = Hash(recoveryPayload),
                recoverySourceAvailable = true,
                fullActivityBackup = snapshot.Kind == "FullPreflight",
                message = "Offline full replace-all payload validated. Repetition writes are not proven against Garmin. No Garmin GET, PUT, or restore performed."
            });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "Edit validation failed against snapshot. No data changed." });
        }
        catch (Exception)
        {
            return Results.Problem("Unable to rehearse snapshot safely.", statusCode: 422);
        }
    }
}
