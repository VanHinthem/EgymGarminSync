using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Persistently rehearses state transitions. NEVER performs an HTTP request to Garmin,
/// and NEVER mutates the mutable Garmin exercise-set cache.
/// This endpoint is for developer-controlled tests, not production write authorization.
/// </summary>
internal static class GarminJournalLifecycle
{
    internal sealed record Request(long GarminId, long JournalId, string Action);
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Gates = new();
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static async Task<IResult> TransitionAsync(Request request, SyncDbContext db)
    {
        if (request.GarminId <= 0 || request.JournalId <= 0 ||
            request.Action is not ("Begin" or "MarkUncertain" or "VerifySimulated"))
            return Results.BadRequest(new { error = "Use Begin, MarkUncertain or VerifySimulated." });

        var gate = Gates.GetOrAdd(request.GarminId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
            return Results.Conflict(new { error = "Activity transition already in progress." });
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var journal = await db.WriteJournal.SingleOrDefaultAsync(x =>
                x.Id == request.JournalId && x.GarminActivityId == request.GarminId);
            if (journal is null) return Results.NotFound();
            var snapshot = await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == journal.FreshSnapshotId && x.GarminActivityId == request.GarminId &&
                x.Kind == "WritePreparation");
            if (snapshot is null || Hash(snapshot.ActivityJson) != snapshot.ActivitySha256 ||
                Hash(snapshot.ExerciseSetsJson) != snapshot.SetsSha256 ||
                snapshot.SetsSha256 != journal.OriginalSha256)
                return Results.Conflict(new { error = "Fresh backup integrity check failed." });

            // Only a completed dry-run can begin another persistent rehearsal.
            // An Uncertain journal is deliberately sticky, including after API restart.
            var before = journal.Status;
            string next;
            switch (request.Action)
            {
                case "Begin" when before == "DryRunVerified":
                    if (await db.WriteJournal.AnyAsync(x => x.GarminActivityId == request.GarminId &&
                        x.Id != journal.Id && (x.Status == "Preparing" || x.Status == "Ready" ||
                         x.Status == "WriteStarted" || x.Status == "Uncertain")))
                        return Results.Conflict(new { error = "Another pending/uncertain journal blocks this activity." });
                    next = "Ready";
                    break;
                case "MarkUncertain" when before == "Ready":
                    next = "Uncertain";
                    break;
                case "VerifySimulated" when before == "Ready":
                    // NO REAL PUT and NO real Garmin readback: this is deliberately
                    // named SimulatedVerified, not Verified.
                    next = "SimulatedVerified";
                    break;
                default:
                    return Results.Conflict(new { error = "Invalid journal transition; terminal/uncertain states cannot be replayed." });
            }
            journal.Status = next;
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            journal.ErrorCode = next == "Uncertain" ? "SIMULATED_UNCERTAIN" : null;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.Ok(new {
                activityId=request.GarminId, journalId=journal.Id,
                previousStatus=before, status=next,
                simulated=true, garminHttpRequests=0, cacheUpdated=false,
                blocksFuturePreparations=next is "Ready" or "Uncertain",
                message="Persisted rehearsal only. No Garmin PUT or readback occurred."
            });
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "Database active-journal constraint rejected transition." });
        }
        finally { gate.Release(); }
    }
}
