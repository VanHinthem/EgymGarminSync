using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Generic save preparation. Performs fresh Garmin GET validation and persists
/// the immutable backup, exact candidate/outgoing payloads and a Ready journal.
/// It never performs the Garmin PUT itself.
/// </summary>
internal static class GarminSaveOrchestrator
{
    internal sealed record Edit(int SetIndex, string Field, decimal Expected, decimal Value);
    internal sealed record Request(long GarminId, long SnapshotId, Edit[] Edits);

    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Locks = new();
    private static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static async Task<IResult> PrepareAsync(Request request, SyncDbContext db)
    {
        if (!OperatingSystem.IsWindows())
            return Results.Problem("Windows DPAPI required.", statusCode: 501);
        if (request.GarminId <= 0 || request.SnapshotId <= 0 ||
            request.Edits is not { Length: > 0 and <= 72 })
            return Results.BadRequest(new { error = "A target, prior backup and edits are required." });
        var gate = Locks.GetOrAdd(request.GarminId, _ => new SemaphoreSlim(1,1));
        if (!await gate.WaitAsync(0))
            return Results.Conflict(new { error = "Another preparation is already active for this Garmin activity." });
        try
        {
            var approved = await db.GarminSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == request.SnapshotId &&
                    s.GarminActivityId == request.GarminId && s.Kind == "FullPreflight");
            if (approved is null || Hash(approved.ActivityJson) != approved.ActivitySha256 ||
                Hash(approved.ExerciseSetsJson) != approved.SetsSha256)
                return Results.Conflict(new { error = "Approved full preflight is missing or damaged." });

            var email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
            var password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                return Results.Problem("Garmin credentials unavailable.",statusCode:503);
            var tokenCache = new WindowsGarminTokenCache(email);
            if (tokenCache.IsInLoginCooldown(out _))
                return Results.Problem("Garmin login cooldown.",statusCode:429);
            using var http = new HttpClient(GarminIpv4Transport.Create());
            var context = new GarminConnectContext(http,
                new BasicAuthParameters(email,password),
                new NotImplementedMfaCode(),tokenCache);
            var client = new GarminConnectClient(context);
            var list = await client.GetActivities(0,100);
            if (!list.Any(a => a.ActivityId == request.GarminId &&
                a.ActivityName is "EGYM Positief" or "EGYM Negatief"))
                return Results.Conflict(new { error = "Garmin target no longer recognized as a recent EGYM activity." });

            var activity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{request.GarminId}")).GetRawText();
            var sets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{request.GarminId}/exerciseSets")).GetRawText();
            using (var activityDoc = JsonDocument.Parse(activity))
                if (!activityDoc.RootElement.TryGetProperty("activityId",out var id) ||
                    !id.TryGetInt64(out var n) || n != request.GarminId)
                    return Results.Conflict(new { error = "Fresh activity identity mismatch." });

            // Require the fresh Garmin state to still match the explicitly reviewed backup.
            if (!GarminFullWeightReadback.Matches(approved.ExerciseSetsJson,sets) ||
                !GarminActivityMetadataTolerance.IsAcceptable(approved.ActivityJson,activity))
                return Results.Conflict(new { error = "Garmin changed since approval; review the activity again." });
            var edits = request.Edits.Select(e =>
                new SetFieldEdit(e.SetIndex,e.Field,e.Expected,e.Value)).ToArray();
            if (edits.All(e => e.Expected == e.Value))
                return Results.BadRequest(new { error = "No changed fields." });
            var candidate = GarminSetFieldDraft.Apply(sets,edits);
            var normalized = GarminTrialPayloadNormalizer.Normalize(candidate,request.GarminId);
            var recovery = GarminTrialPayloadNormalizer.Normalize(sets,request.GarminId);
            GarminExerciseSetsWriteContract.Validate(normalized,request.GarminId);
            GarminExerciseSetsWriteContract.Validate(recovery,request.GarminId);

            // Store the fresh backup BEFORE a future writer is ever allowed to proceed.
            // An existing immutable snapshot is never updated.
            var backup = new GarminSnapshot {
                GarminActivityId=request.GarminId, Kind="WritePreparation",
                CapturedUtc=DateTimeOffset.UtcNow,
                ActivityJson=activity, ActivitySha256=Hash(activity),
                ExerciseSetsJson=sets, SetsSha256=Hash(sets)
            };
            // A write attempt left in an uncertain or pending state blocks further
            // preparation. This guard is backed by a SQLite partial unique index,
            // not merely by the in-process semaphore.
            if (await db.WriteJournal.AnyAsync(x => x.GarminActivityId == request.GarminId &&
                (x.Status == "Preparing" || x.Status == "Ready" ||
                 x.Status == "WriteStarted" || x.Status == "Uncertain")))
                return Results.Conflict(new { error = "An unresolved Garmin write exists. Inspect the journal; no automatic retry." });

            await using var transaction = await db.Database.BeginTransactionAsync();
            db.GarminSnapshots.Add(backup);
            await db.SaveChangesAsync();
            var journal = new GarminWriteJournal {
                GarminActivityId = request.GarminId,
                ApprovedSnapshotId = request.SnapshotId,
                FreshSnapshotId = backup.Id,
                Status = "Ready",
                OriginalSha256 = Hash(sets),
                CandidateSha256 = Hash(candidate),
                ProposedPayloadSha256 = Hash(normalized),
                RecoveryPayloadSha256 = Hash(recovery),
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            db.WriteJournal.Add(journal);
            await db.SaveChangesAsync();
            db.WritePayloads.Add(new GarminWritePayload {
                JournalId = journal.Id,
                GarminActivityId = request.GarminId,
                CandidateJson = candidate,
                OutgoingJson = normalized,
                CandidateSha256 = Hash(candidate),
                OutgoingSha256 = Hash(normalized)
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            tokenCache.ClearLoginCooldown();
            return Results.Ok(new {
                readOnly=false, canWrite=GarminWriteTransportAdapter.LiveWriteEnabled, activityId=request.GarminId,
                sourceBackupId=request.SnapshotId, freshBackupId=backup.Id,
                journalId=journal.Id, journalStatus=journal.Status,
                fieldsChanged=edits.Count(e=>e.Expected!=e.Value),
                originalSha256=Hash(sets), candidateSha256=Hash(candidate),
                normalizedSha256=Hash(normalized), recoverySha256=Hash(recovery),
                concurrencyCheckPassed=true, restoreContractValid=true,
                message="Fresh live state verified; immutable backup, exact payloads and Ready journal recorded. NO PUT."
            });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "Selected fields no longer match Garmin or payload validation failed." });
        }
        catch (Exception)
        {
            return Results.Problem("Garmin write preparation failed; no write attempted.",statusCode:502);
        }
        finally { gate.Release(); }
    }
}
