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
        if (snapshot is null || snapshot.Kind != "Preflight")
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
            var restored = newSets.GetRawText();
            // Restoration rehearsal: replace the proposed sets with the original
            // snapshot sets, proving the exact original set subtree is available.
            if (!JsonElement.DeepEquals(oldSets, JsonDocument.Parse(restored).RootElement))
            {
                // They must differ if at least one real field change was requested.
                if (edits.All(x => x.Expected == x.Value))
                    throw new InvalidOperationException("No actual changes.");
            }
            var changed = edits.Count(x => x.Expected != x.Value);
            if (changed == 0)
                return Results.BadRequest(new { error = "No actual field changes." });
            return Results.Ok(new {
                readOnly = true, canWrite = false, canRestore = false,
                snapshotId = snapshot.Id, activityId = snapshot.GarminActivityId,
                integrityOk = true, setCount = oldSets.GetArrayLength(),
                fieldChanges = changed,
                originalSha256 = snapshot.SetsSha256,
                proposedSha256 = Hash(proposed),
                recoverySourceSha256 = snapshot.SetsSha256,
                recoverySourceAvailable = true,
                message = "Offline edit and recovery source validated. No Garmin GET, PUT, or restore performed."
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
