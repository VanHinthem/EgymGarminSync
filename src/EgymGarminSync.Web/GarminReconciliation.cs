using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Read-only comparison of an immutable original, planned sets, and observed sets.
/// No Garmin transport, journal mutation or cache writes.</summary>
internal static class GarminReconciliation
{
    internal sealed record Edit(int SetIndex, string Field, decimal Expected, decimal Value);
    internal sealed record Request(long GarminId, long JournalId, Edit[] Edits, string ObservedScenario);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Classify(string original, string candidate, string observed)
    {
        bool before = GarminFullWeightReadback.Matches(original, observed);
        bool after = GarminFullWeightReadback.Matches(candidate, observed);
        // Ambiguous / inconsistent results cannot silently clear an uncertain write.
        if (before == after) return "Uncertain";
        return after ? "Verified" : "NotApplied";
    }

    public static async Task<IResult> SimulateAsync(Request request, SyncDbContext db)
    {
        if (request.GarminId <= 0 || request.JournalId <= 0 ||
            request.Edits is not { Length: > 0 and <= 72 } ||
            request.ObservedScenario is not ("Original" or "Candidate" or "Different"))
            return Results.BadRequest(new { error = "Expected Original, Candidate or Different and valid edits." });

        var journal = await db.WriteJournal.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.JournalId && x.GarminActivityId == request.GarminId);
        if (journal is null || journal.Status != "Uncertain")
            return Results.Conflict(new { error = "An existing Uncertain journal is required." });

        var backup = await db.GarminSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == journal.FreshSnapshotId && x.GarminActivityId == request.GarminId &&
            x.Kind == "WritePreparation");
        if (backup is null || Hash(backup.ActivityJson) != backup.ActivitySha256 ||
            Hash(backup.ExerciseSetsJson) != backup.SetsSha256 ||
            backup.SetsSha256 != journal.OriginalSha256)
            return Results.Conflict(new { error = "Immutable backup integrity failed." });

        try
        {
            var edits = request.Edits.Select(x => new SetFieldEdit(
                x.SetIndex, x.Field, x.Expected, x.Value)).ToArray();
            if (edits.All(x => x.Expected == x.Value))
                return Results.BadRequest(new { error = "No actual edit supplied." });
            var candidate = GarminSetFieldDraft.Apply(backup.ExerciseSetsJson,edits);
            var normalized = GarminTrialPayloadNormalizer.Normalize(candidate,request.GarminId);
            GarminExerciseSetsWriteContract.Validate(normalized,request.GarminId);
            if (Hash(candidate) != journal.CandidateSha256 ||
                Hash(normalized) != journal.ProposedPayloadSha256)
                return Results.Conflict(new { error = "Edits do not match the journaled candidate." });

            // Deliberately synthetic readback: this is not a Garmin API operation.
            var observed = request.ObservedScenario switch {
                "Original" => backup.ExerciseSetsJson,
                "Candidate" => candidate,
                _ => "{}"
            };
            string decision = request.ObservedScenario == "Different"
                ? "Uncertain" : Classify(backup.ExerciseSetsJson,candidate,observed);
            return Results.Ok(new {
                simulated=true, readOnly=true, garminHttpRequests=0,
                sqliteWrites=0, cacheUpdated=false,
                activityId=request.GarminId, journalId=journal.Id,
                observedScenario=request.ObservedScenario,
                decision, wouldClearBlock=decision!="Uncertain",
                wouldUpdateCache=decision=="Verified",
                currentJournalStatus=journal.Status,
                note="Reconciliation classification only; no persisted transition or live Garmin readback."
            });
        }
        catch(Exception e) when (e is InvalidOperationException or ArgumentException or JsonException)
        {
            return Results.Conflict(new { error="Candidate reconstruction failed." });
        }
    }
}
