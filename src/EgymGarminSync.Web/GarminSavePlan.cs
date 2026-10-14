using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Matching;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Generic, offline validation and backup-backed recovery planning.
/// No Garmin transport is referenced; the web write endpoint remains disabled.
/// </summary>
internal static class GarminSavePlan
{
    internal sealed record Edit(int SetIndex, string Field, decimal Expected, decimal Value);
    internal sealed record Request(long GarminId, long SnapshotId, Edit[] Edits);

    private static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static async Task<IResult> PlanAsync(Request request, SyncDbContext db)
    {
        if (request.GarminId <= 0 || request.SnapshotId <= 0 ||
            request.Edits is not { Length: > 0 and <= 72 })
            return Results.BadRequest(new { error = "Activity, full backup and 1..72 edits required." });

        var backup = await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.SnapshotId && x.GarminActivityId == request.GarminId &&
                x.Kind == "FullPreflight");
        if (backup is null)
            return Results.Conflict(new { error = "Matching full-activity preflight backup required." });
        if (Hash(backup.ExerciseSetsJson) != backup.SetsSha256 ||
            Hash(backup.ActivityJson) != backup.ActivitySha256)
            return Results.Conflict(new { error = "Backup integrity mismatch." });

        try
        {
            using var activity = JsonDocument.Parse(backup.ActivityJson);
            if (!activity.RootElement.TryGetProperty("activityId", out var activityId) ||
                !activityId.TryGetInt64(out var id) || id != request.GarminId)
                return Results.Conflict(new { error = "Backup activity identity mismatch." });

            var edits = request.Edits.Select(e =>
                new SetFieldEdit(e.SetIndex, e.Field, e.Expected, e.Value)).ToArray();
            if (edits.All(e => e.Expected == e.Value))
                return Results.BadRequest(new { error = "No actual changes selected." });

            var candidate = GarminSetFieldDraft.Apply(backup.ExerciseSetsJson, edits);
            var payload = GarminTrialPayloadNormalizer.Normalize(candidate, request.GarminId);
            var recovery = GarminTrialPayloadNormalizer.Normalize(
                backup.ExerciseSetsJson, request.GarminId);
            GarminExerciseSetsWriteContract.Validate(payload, request.GarminId);
            GarminExerciseSetsWriteContract.Validate(recovery, request.GarminId);

            return Results.Ok(new {
                readOnly = true, canWrite = false, canRestore = false,
                activityId = request.GarminId, backupId = backup.Id,
                capturedUtc = backup.CapturedUtc,
                changes = edits.Where(e => e.Expected != e.Value).Select(e =>
                    new { e.SetIndex, e.Field, from = e.Expected, to = e.Value }).ToArray(),
                beforeSha256 = backup.SetsSha256,
                candidateSha256 = Hash(candidate),
                normalizedPayloadSha256 = Hash(payload),
                recoveryPayloadSha256 = Hash(recovery),
                backupIntegrityOk = true, writeContractValid = true,
                recoveryContractValid = true,
                message = "Generic offline save plan only. A future writer must capture another fresh backup immediately before a PUT and verify all data afterward."
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            return Results.Conflict(new { error = "Selected edits or full-backup schema failed validation." });
        }
    }
}
