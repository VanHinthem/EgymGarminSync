using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Matching;
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
                var sets = BuildSyntheticSets(activityId);
                var backup = new GarminSnapshot {
                    GarminActivityId=activityId, Kind="WritePreparation",
                    CapturedUtc=DateTimeOffset.UtcNow,
                    ActivityJson=activity, ActivitySha256=Hash(activity),
                    ExerciseSetsJson=sets, SetsSha256=Hash(sets)
                };
                db.GarminSnapshots.Add(backup);
                await db.SaveChangesAsync();
                var candidate = GarminSetFieldDraft.Apply(sets,
                    new[] { new SetFieldEdit(2, "repetitionCount", 15, 14) });
                var normalized = GarminTrialPayloadNormalizer.Normalize(candidate, activityId);
                var first = new GarminWriteJournal {
                    GarminActivityId=activityId, ApprovedSnapshotId=backup.Id,
                    FreshSnapshotId=backup.Id, Status="DryRunVerified",
                    OriginalSha256=backup.SetsSha256,
                    CandidateSha256=Hash(candidate),
                    ProposedPayloadSha256=Hash(normalized),
                    RecoveryPayloadSha256=backup.SetsSha256,
                    CreatedUtc=DateTimeOffset.UtcNow, UpdatedUtc=DateTimeOffset.UtcNow
                };
                var second = new GarminWriteJournal {
                    GarminActivityId=activityId, ApprovedSnapshotId=backup.Id,
                    FreshSnapshotId=backup.Id, Status="DryRunVerified",
                    OriginalSha256=backup.SetsSha256,
                    CandidateSha256=Hash(candidate),
                    ProposedPayloadSha256=Hash(normalized),
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

                var initialJournalCount=await reopened.WriteJournal.CountAsync();
                var initialCacheCount=await reopened.GarminSets.CountAsync();
                var expected=new Dictionary<string,string> {
                    ["Original"]="NotApplied",
                    ["Candidate"]="Verified",
                    ["Different"]="Uncertain"
                };
                foreach(var testCase in expected)
                {
                    var response=await GarminReconciliation.SimulateAsync(
                        new(activityId,firstJournalId,
                            new[] { new GarminReconciliation.Edit(2,"repetitionCount",15,14) },
                            testCase.Key),reopened);
                    if(response is not IValueHttpResult value ||
                       response is not IStatusCodeHttpResult { StatusCode:200 })
                        throw new InvalidOperationException("Reconciliation "+testCase.Key+" rejected.");
                    using var responseDoc=JsonDocument.Parse(JsonSerializer.Serialize(value.Value));
                    var root=responseDoc.RootElement;
                    if(root.GetProperty("decision").GetString()!=testCase.Value ||
                       root.GetProperty("sqliteWrites").GetInt32()!=0 ||
                       root.GetProperty("garminHttpRequests").GetInt32()!=0 ||
                       root.GetProperty("cacheUpdated").GetBoolean())
                        throw new InvalidOperationException("Reconciliation "+testCase.Key+" incorrect.");
                    var stillUncertain=await reopened.WriteJournal.AsNoTracking()
                        .SingleAsync(x=>x.Id==firstJournalId);
                    if(stillUncertain.Status!="Uncertain" ||
                       await reopened.WriteJournal.CountAsync()!=initialJournalCount ||
                       await reopened.GarminSets.CountAsync()!=initialCacheCount)
                        throw new InvalidOperationException("Reconciliation mutated persistent state.");
                    Console.WriteLine("PASS: "+testCase.Key+" -> "+testCase.Value+"; journal remains Uncertain.");
                }
                var syntheticBackup=await reopened.GarminSnapshots.AsNoTracking()
                    .SingleAsync(x=>x.Id==uncertain.FreshSnapshotId);
                var changedSets=GarminSetFieldDraft.Apply(syntheticBackup.ExerciseSetsJson,
                    new[] { new SetFieldEdit(2,"repetitionCount",15,14) });
                var liveTests=new[] {
                    (Name:"Original", Sets:syntheticBackup.ExerciseSetsJson,
                        Activity:syntheticBackup.ActivityJson, Decision:"NotApplied", Metadata:true),
                    (Name:"Candidate", Sets:changedSets,
                        Activity:syntheticBackup.ActivityJson, Decision:"Verified", Metadata:true),
                    (Name:"Different", Sets:GarminSetFieldDraft.Apply(syntheticBackup.ExerciseSetsJson,
                        new[] { new SetFieldEdit(2,"repetitionCount",15,13) }),
                        Activity:syntheticBackup.ActivityJson, Decision:"Uncertain", Metadata:true),
                    (Name:"BadMetadata", Sets:changedSets,
                        Activity:"""{"activityId":123}""", Decision:"Uncertain", Metadata:false)
                };
                foreach(var testCase in liveTests)
                {
                    var result=await GarminLiveReconciliation.EvaluateAsync(
                        new(activityId,firstJournalId,new[] {
                            new GarminReconciliation.Edit(2,"repetitionCount",15,14)
                        }),reopened,
                        _=>Task.FromResult(new GarminLiveReconciliation.Observation(
                            testCase.Activity,testCase.Sets)));
                    if(result is not IValueHttpResult typed ||
                        result is not IStatusCodeHttpResult {StatusCode:200})
                        throw new InvalidOperationException("GET-only "+testCase.Name+" rejected.");
                    using var json=JsonDocument.Parse(JsonSerializer.Serialize(typed.Value));
                    var response=json.RootElement;
                    if(response.GetProperty("decision").GetString()!=testCase.Decision ||
                       response.GetProperty("metadataOk").GetBoolean()!=testCase.Metadata ||
                       response.GetProperty("cacheUpdated").GetBoolean() ||
                       response.GetProperty("garminPutAttempted").GetBoolean())
                        throw new InvalidOperationException("GET-only "+testCase.Name+" incorrect.");
                    Console.WriteLine("PASS: mocked GET "+testCase.Name+" -> "+testCase.Decision+".");
                }
                var failure=await GarminLiveReconciliation.EvaluateAsync(
                    new(activityId,firstJournalId,new[] {
                        new GarminReconciliation.Edit(2,"repetitionCount",15,14)
                    }),reopened,_=>Task.FromException<GarminLiveReconciliation.Observation>(
                        new HttpRequestException("Synthetic GET failure")));
                if(failure is not IStatusCodeHttpResult {StatusCode:502})
                    throw new InvalidOperationException("GET failure was not rejected.");
                var afterLive=await reopened.WriteJournal.AsNoTracking()
                    .SingleAsync(x=>x.Id==firstJournalId);
                if(afterLive.Status!="Uncertain" ||
                   await reopened.WriteJournal.CountAsync()!=initialJournalCount ||
                   await reopened.GarminSets.CountAsync()!=initialCacheCount)
                    throw new InvalidOperationException("Mock GET mutated persistent state.");
                Console.WriteLine("PASS: mocked GET failure -> HTTP 502; journal stays Uncertain.");
                // Exercise atomic simulated finalization only after all read-only and
                // lock assertions. No HTTP endpoint exposes this test helper.
                var different=GarminSetFieldDraft.Apply(syntheticBackup.ExerciseSetsJson,
                    new[] {new SetFieldEdit(2,"repetitionCount",15,13)});
                var unresolved=await GarminReconciliationFinalizer.FinalizeIsolatedAsync(
                    reopened,activityId,firstJournalId,syntheticBackup.ExerciseSetsJson,
                    changedSets,different,syntheticBackup.ActivityJson);
                if(unresolved!="Uncertain")
                    throw new InvalidOperationException("Different observation improperly finalized.");
                var verified=await GarminReconciliationFinalizer.FinalizeIsolatedAsync(
                    reopened,activityId,firstJournalId,syntheticBackup.ExerciseSetsJson,
                    changedSets,changedSets,syntheticBackup.ActivityJson);
                if(verified!="Verified")
                    throw new InvalidOperationException("Verified observation failed to finalize.");
                var stored=await reopened.GarminSets.AsNoTracking()
                    .SingleOrDefaultAsync(x=>x.GarminActivityId==activityId);
                if(stored is null || stored.Sha256!=Hash(changedSets) ||
                    (await reopened.WriteJournal.AsNoTracking().SingleAsync(
                        x=>x.Id==firstJournalId)).Status!="Verified")
                    throw new InvalidOperationException("Verified/cache atomic result incorrect.");
                var repeat=await GarminReconciliationFinalizer.FinalizeIsolatedAsync(
                    reopened,activityId,firstJournalId,syntheticBackup.ExerciseSetsJson,
                    changedSets,changedSets,syntheticBackup.ActivityJson);
                if(repeat!="Blocked")
                    throw new InvalidOperationException("Repeated finalization not blocked.");
                Console.WriteLine("PASS: isolated Different stays Uncertain; Verified atomically updates journal/cache.");
                Console.WriteLine("PASS: repeated finalization rejected.");

                // A second isolated journal can now become Uncertain, then NotApplied;
                // no cache mutation must be performed for NotApplied.
                await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                    new(activityId,secondJournalId,"Begin"),reopened),"Second Begin");
                await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                    new(activityId,secondJournalId,"MarkUncertain"),reopened),"Second Uncertain");
                var cacheHashBefore=(await reopened.GarminSets.AsNoTracking()
                    .SingleAsync(x=>x.GarminActivityId==activityId)).Sha256;
                var notApplied=await GarminReconciliationFinalizer.FinalizeIsolatedAsync(
                    reopened,activityId,secondJournalId,syntheticBackup.ExerciseSetsJson,
                    changedSets,syntheticBackup.ExerciseSetsJson,syntheticBackup.ActivityJson);
                var cacheHashAfter=(await reopened.GarminSets.AsNoTracking()
                    .SingleAsync(x=>x.GarminActivityId==activityId)).Sha256;
                if(notApplied!="NotApplied" || cacheHashBefore!=cacheHashAfter)
                    throw new InvalidOperationException("NotApplied modified cached sets.");
                Console.WriteLine("PASS: isolated NotApplied closes journal without cache update.");
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

    private static string BuildSyntheticSets(long activityId)
    {
        var sets = Enumerable.Range(0,36).Select(i => new {
            setType="ACTIVE",
            startTime="2026-10-15T06:00:00Z",
            repetitionCount=15,
            weight=20000,
            exercises=new[] { new { exerciseName="SYNTHETIC_TEST" } }
        }).ToArray();
        return JsonSerializer.Serialize(new { activityId, exerciseSets=sets });
    }

    private static async Task RequireSuccess(Task<IResult> action,string step)
    {
        var result=await action;
        if(result is not IStatusCodeHttpResult { StatusCode:200 })
            throw new InvalidOperationException(step+" failed.");
    }
}
