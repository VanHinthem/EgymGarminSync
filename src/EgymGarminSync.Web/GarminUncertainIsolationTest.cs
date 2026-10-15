using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Isolated integration rehearsal: disposable SQLite file, no Garmin client,
/// no access to the user's normal sync.db or any saved activities.
/// </summary>
internal static class GarminUncertainIsolationTest
{
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async Task<int> RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "egym-journal-test-" + Guid.NewGuid().ToString("N") + ".db");
        const long activityId = 9876543210;
        var options = new DbContextOptionsBuilder<SyncDbContext>()
            .UseSqlite("Data Source=" + path).Options;

        try
        {
            long firstJournalId;
            long secondJournalId;
            await using (var db = new SyncDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var activity = "{\"activityId\":9876543210}";
                var sets = "{\"exerciseSets\":[]}";
                var backup = new GarminSnapshot {
                    GarminActivityId=activityId, Kind="WritePreparation",
                    CapturedUtc=DateTimeOffset.UtcNow,
                    ActivityJson=activity, ActivitySha256=Hash(activity),
                    ExerciseSetsJson=sets, SetsSha256=Hash(sets)
                };
                db.GarminSnapshots.Add(backup);
                await db.SaveChangesAsync();
                var first = new GarminWriteJournal {
                    GarminActivityId=activityId, ApprovedSnapshotId=backup.Id,
                    FreshSnapshotId=backup.Id, Status="DryRunVerified",
                    OriginalSha256=backup.SetsSha256,
                    CandidateSha256=backup.SetsSha256,
                    ProposedPayloadSha256=backup.SetsSha256,
                    RecoveryPayloadSha256=backup.SetsSha256,
                    CreatedUtc=DateTimeOffset.UtcNow, UpdatedUtc=DateTimeOffset.UtcNow
                };
                var second = new GarminWriteJournal {
                    GarminActivityId=activityId, ApprovedSnapshotId=backup.Id,
                    FreshSnapshotId=backup.Id, Status="DryRunVerified",
                    OriginalSha256=backup.SetsSha256,
                    CandidateSha256=backup.SetsSha256,
                    ProposedPayloadSha256=backup.SetsSha256,
                    RecoveryPayloadSha256=backup.SetsSha256,
                    CreatedUtc=DateTimeOffset.UtcNow, UpdatedUtc=DateTimeOffset.UtcNow
                };
                db.WriteJournal.AddRange(first,second);
                await db.SaveChangesAsync();
                firstJournalId=first.Id;
                secondJournalId=second.Id;
                await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                    new(activityId,firstJournalId,"Begin"),db),"Begin");
                await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                    new(activityId,firstJournalId,"MarkUncertain"),db),"MarkUncertain");

                var uncertain=await db.WriteJournal.AsNoTracking()
                    .SingleAsync(x=>x.Id==firstJournalId);
                if(uncertain.Status!="Uncertain")
                    throw new InvalidOperationException("Uncertain not saved.");
            }

            // Open a NEW EF context to emulate an API restart.
            await using (var reopened = new SyncDbContext(options))
            {
                var uncertain=await reopened.WriteJournal.AsNoTracking()
                    .SingleAsync(x=>x.Id==firstJournalId);
                if(uncertain.Status!="Uncertain")
                    throw new InvalidOperationException("Uncertain did not survive restart.");

                var blocked = await GarminJournalLifecycle.TransitionAsync(
                    new(activityId,secondJournalId,"Begin"),reopened);
                if(blocked is not IStatusCodeHttpResult { StatusCode:409 })
                    throw new InvalidOperationException("Second write was not rejected.");

                var second=await reopened.WriteJournal.AsNoTracking().SingleAsync(x=>x.Id==secondJournalId);
                if(second.Status!="DryRunVerified")
                    throw new InvalidOperationException("Blocked journal status changed.");

                var repeated=await GarminJournalLifecycle.TransitionAsync(
                    new(activityId,firstJournalId,"Begin"),reopened);
                if(repeated is not IStatusCodeHttpResult { StatusCode:409 })
                    throw new InvalidOperationException("Uncertain journal was restarted.");

                Console.WriteLine("PASS: Uncertain persisted after reopened SQLite connection.");
                Console.WriteLine("PASS: Second journal blocked and remained DryRunVerified.");
                Console.WriteLine("PASS: Uncertain journal cannot be restarted.");
                Console.WriteLine("ISOLATED ONLY: no Garmin HTTP, no changes to sync.db, no PUT.");
            }
            return 0;
        }
        catch(Exception e)
        {
            Console.Error.WriteLine("ISOLATION TEST FAILED: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }
        finally
        {
            foreach(var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path+suffix); } catch(IOException) { /* test artifact only */ }
            }
        }
    }

    private static async Task RequireSuccess(Task<IResult> action,string step)
    {
        var result=await action;
        if(result is not IStatusCodeHttpResult { StatusCode:200 })
            throw new InvalidOperationException(step+" failed.");
    }
}
