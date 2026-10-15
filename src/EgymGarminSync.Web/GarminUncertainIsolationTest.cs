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
                foreach(var scenario in new[] {"Success","Timeout","Mismatch"})
                {
                    var trialActivityId=activityId+scenario.Length+scenario[0];
                    var trialActivity=JsonSerializer.Serialize(new {activityId=trialActivityId});
                    var trialSets=BuildSyntheticSets(trialActivityId);
                    var trialCandidate=GarminSetFieldDraft.Apply(trialSets,
                        new[] {new SetFieldEdit(2,"repetitionCount",15,14)});
                    var trialOutgoing=GarminTrialPayloadNormalizer.Normalize(trialCandidate,trialActivityId);
                    var snapshot=new GarminSnapshot {
                        GarminActivityId=trialActivityId,Kind="WritePreparation",
                        ActivityJson=trialActivity,ActivitySha256=Hash(trialActivity),
                        ExerciseSetsJson=trialSets,SetsSha256=Hash(trialSets),
                        CapturedUtc=DateTimeOffset.UtcNow
                    };
                    reopened.GarminSnapshots.Add(snapshot);
                    await reopened.SaveChangesAsync();
                    var trialJournal=new GarminWriteJournal {
                        GarminActivityId=trialActivityId,ApprovedSnapshotId=snapshot.Id,
                        FreshSnapshotId=snapshot.Id,Status="DryRunVerified",
                        OriginalSha256=Hash(trialSets),CandidateSha256=Hash(trialCandidate),
                        ProposedPayloadSha256=Hash(trialOutgoing),
                        RecoveryPayloadSha256=Hash(trialSets),
                        CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow
                    };
                    reopened.WriteJournal.Add(trialJournal);
                    await reopened.SaveChangesAsync();
                    if(!await GarminTransactionDryRun.PersistSyntheticPayloadAsync(reopened,
                        trialActivityId,trialJournal.Id,trialCandidate,trialOutgoing))
                        throw new InvalidOperationException("Durable payload persistence failed.");
                    await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                        new(trialActivityId,trialJournal.Id,"Begin"),reopened),"Dry run Begin");
                    var observed=trialSets;
                    var calls=0;
                    try
                    {
                        var outcome=await GarminTransactionDryRun.ExecuteAsync(
                            reopened,trialActivityId,trialJournal.Id,
                            _=>Task.FromResult(new GarminTransactionDryRun.Observation(trialActivity,observed)),
                            (_,outgoing)=>GarminWriteTransportAdapter.SendAsync(
                                trialActivityId,outgoing,(id,fullPayload)=>{
                                    calls++;
                                    var tracked=reopened.WriteJournal.AsNoTracking()
                                        .Single(x=>x.Id==trialJournal.Id);
                                    if(tracked.Status!="WriteStarted" || id!=trialActivityId)
                                        throw new InvalidOperationException("WriteStarted not durable before mock transport.");
                                    if(scenario=="Timeout") throw new TimeoutException("synthetic timeout");
                                    observed=scenario=="Success" ? trialCandidate
                                        : GarminSetFieldDraft.Apply(trialSets,
                                            new[] {new SetFieldEdit(2,"repetitionCount",15,13)});
                                    return Task.CompletedTask;
                                }));
                        var expectedStatus=scenario=="Success" ? "SimulatedVerified" : "Uncertain";
                        var persisted=await reopened.WriteJournal.AsNoTracking()
                            .SingleAsync(x=>x.Id==trialJournal.Id);
                        if(outcome!=expectedStatus || persisted.Status!=expectedStatus || calls!=1)
                            throw new InvalidOperationException("Dry run "+scenario+" status or count incorrect.");
                        var duplicate=await GarminTransactionDryRun.ExecuteAsync(
                            reopened,trialActivityId,trialJournal.Id,
                            _=>Task.FromResult(new GarminTransactionDryRun.Observation(trialActivity,observed)),
                            (_,_)=>throw new InvalidOperationException("Duplicate writer invoked"));
                        if(duplicate!="Blocked" || calls!=1)
                            throw new InvalidOperationException("Duplicate dry run was accepted.");
                        if(await reopened.GarminSets.AnyAsync(x=>x.GarminActivityId==trialActivityId))
                            throw new InvalidOperationException("Simulation modified cache.");
                        Console.WriteLine("PASS: simulated transaction "+scenario+
                            " -> "+expectedStatus+"; one synthetic write, duplicates refused.");
                    }
                    finally { /* Durable payload intentionally preserved for recovery. */ }
                }
                // Emulate process death AFTER committed WriteStarted and BEFORE readback.
                var crashId=activityId+8000;
                var crashActivity=JsonSerializer.Serialize(new {activityId=crashId});
                var crashSets=BuildSyntheticSets(crashId);
                var crashCandidate=GarminSetFieldDraft.Apply(crashSets,
                    new[] {new SetFieldEdit(2,"repetitionCount",15,14)});
                var crashOutgoing=GarminTrialPayloadNormalizer.Normalize(crashCandidate,crashId);
                var crashBackup=new GarminSnapshot {
                    GarminActivityId=crashId,Kind="WritePreparation",
                    ActivityJson=crashActivity,ActivitySha256=Hash(crashActivity),
                    ExerciseSetsJson=crashSets,SetsSha256=Hash(crashSets),
                    CapturedUtc=DateTimeOffset.UtcNow
                };
                reopened.GarminSnapshots.Add(crashBackup);
                await reopened.SaveChangesAsync();
                var crashEntry=new GarminWriteJournal {
                    GarminActivityId=crashId,ApprovedSnapshotId=crashBackup.Id,
                    FreshSnapshotId=crashBackup.Id,Status="DryRunVerified",
                    OriginalSha256=Hash(crashSets),CandidateSha256=Hash(crashCandidate),
                    ProposedPayloadSha256=Hash(crashOutgoing),
                    RecoveryPayloadSha256=Hash(crashSets),
                    CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow
                };
                reopened.WriteJournal.Add(crashEntry);
                await reopened.SaveChangesAsync();
                if(!await GarminTransactionDryRun.PersistSyntheticPayloadAsync(reopened,
                    crashId,crashEntry.Id,crashCandidate,crashOutgoing))
                    throw new InvalidOperationException("Crash payload not persisted.");
                await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                    new(crashId,crashEntry.Id,"Begin"),reopened),"Crash Begin");
                // Separate process emulation: committed state changed to WriteStarted.
                var crashTracked=await reopened.WriteJournal.SingleAsync(x=>x.Id==crashEntry.Id);
                crashTracked.Status="WriteStarted";
                await reopened.SaveChangesAsync();
                reopened.ChangeTracker.Clear();
                if(await GarminCrashRecovery.RecoverAsync(reopened)!=0 ||
                    (await reopened.WriteJournal.AsNoTracking().SingleAsync(x=>x.Id==crashEntry.Id)).Status!="WriteStarted")
                    throw new InvalidOperationException("Startup interfered with a possible active writer.");
                Console.WriteLine("PASS: second API startup preserves possibly active WriteStarted.");
                if(await GarminCrashRecovery.RecoverAsync(reopened,exclusiveWriterShutdownConfirmed:true)!=1)
                    throw new InvalidOperationException("Crash recovery count incorrect.");
                if(await GarminCrashRecovery.RecoverAsync(reopened,exclusiveWriterShutdownConfirmed:true)!=0)
                    throw new InvalidOperationException("Crash recovery was not idempotent.");
                var recovered=await reopened.WriteJournal.AsNoTracking()
                    .SingleAsync(x=>x.Id==crashEntry.Id);
                var durable=await reopened.WritePayloads.AsNoTracking()
                    .SingleAsync(x=>x.JournalId==crashEntry.Id);
                if(recovered.Status!="Uncertain" ||
                    durable.CandidateSha256!=Hash(crashCandidate) ||
                    durable.OutgoingSha256!=Hash(crashOutgoing))
                    throw new InvalidOperationException("Crash recovery or durable payload invalid.");
                var retried=await GarminTransactionDryRun.ExecuteAsync(reopened,crashId,crashEntry.Id,
                    _=>throw new InvalidOperationException("Read must not happen on retry"),
                    (_,_)=>throw new InvalidOperationException("Write must not happen on retry"));
                if(retried!="Blocked")
                    throw new InvalidOperationException("Recovered write could be retried.");
                Console.WriteLine("PASS: interrupted WriteStarted -> persistent Uncertain; repeat recovery safe.");
                Console.WriteLine("PASS: recovery payload survives and synthetic writer cannot retry.");
                // Independent EF context against the SAME SQLite file represents
                // another API process. SQLite unique partial index must reject its
                // attempt to claim a second active journal for the same activity.
                await using(var competing = new SyncDbContext(options))
                {
                    competing.WriteJournal.Add(new GarminWriteJournal {
                        GarminActivityId=crashId,ApprovedSnapshotId=crashBackup.Id,
                        FreshSnapshotId=crashBackup.Id,Status="Ready",
                        OriginalSha256=Hash(crashSets),CandidateSha256=Hash(crashCandidate),
                        ProposedPayloadSha256=Hash(crashOutgoing),
                        RecoveryPayloadSha256=Hash(crashSets),
                        CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow
                    });
                    bool rejected=false;
                    try { await competing.SaveChangesAsync(); }
                    catch(DbUpdateException) { rejected=true; }
                    if(!rejected)
                        throw new InvalidOperationException("Independent writer bypassed SQLite activity constraint.");
                }
                Console.WriteLine("PASS: independent SQLite context cannot claim competing active journal.");

                // Race two independently scoped DbContexts claiming different
                // journal rows for the same synthetic activity.
                var raceId=activityId+9000;
                var raceActivity=JsonSerializer.Serialize(new {activityId=raceId});
                var raceSets=BuildSyntheticSets(raceId);
                var raceSnapshot=new GarminSnapshot {
                    GarminActivityId=raceId, Kind="WritePreparation",
                    ActivityJson=raceActivity, ActivitySha256=Hash(raceActivity),
                    ExerciseSetsJson=raceSets, SetsSha256=Hash(raceSets),
                    CapturedUtc=DateTimeOffset.UtcNow
                };
                reopened.GarminSnapshots.Add(raceSnapshot);
                await reopened.SaveChangesAsync();
                var raceRows=Enumerable.Range(0,2).Select(_=>new GarminWriteJournal {
                    GarminActivityId=raceId, ApprovedSnapshotId=raceSnapshot.Id,
                    FreshSnapshotId=raceSnapshot.Id, Status="DryRunVerified",
                    OriginalSha256=Hash(raceSets), CandidateSha256=Hash(raceSets),
                    ProposedPayloadSha256=Hash(raceSets), RecoveryPayloadSha256=Hash(raceSets),
                    CreatedUtc=DateTimeOffset.UtcNow, UpdatedUtc=DateTimeOffset.UtcNow
                }).ToArray();
                reopened.WriteJournal.AddRange(raceRows);
                await reopened.SaveChangesAsync();
                reopened.ChangeTracker.Clear();
                using(var start=new ManualResetEventSlim(false))
                {
                    var claims=raceRows.Select(row=>Task.Run(async ()=>{
                        using var contender=new SyncDbContext(options);
                        start.Wait();
                        try {
                            var result=await GarminJournalLifecycle.TransitionAsync(
                                new(raceId,row.Id,"Begin"),contender);
                            return result is IStatusCodeHttpResult {StatusCode:200};
                        }
                        catch(DbUpdateException) { return false; }
                        catch(Microsoft.Data.Sqlite.SqliteException e) when(e.SqliteErrorCode==5 ||
                            e.SqliteErrorCode==19) { return false; }
                    })).ToArray();
                    start.Set();
                    var outcomes=await Task.WhenAll(claims);
                    var persisted=await reopened.WriteJournal.AsNoTracking()
                        .Where(x=>x.GarminActivityId==raceId && x.Status=="Ready")
                        .CountAsync();
                    if(outcomes.Count(x=>x)!=1 || persisted!=1)
                        throw new InvalidOperationException("Concurrent claim did not produce exactly one owner.");
                }
                Console.WriteLine("PASS: concurrent independent SQLite contexts permit exactly one Ready claim.");
                // Real OS process race, using two fresh journal rows against
                // the same disposable SQLite file. Neither process can write Garmin.
                var processId=activityId+10000;
                var processActivity=JsonSerializer.Serialize(new {activityId=processId});
                var processSets=BuildSyntheticSets(processId);
                var processBackup=new GarminSnapshot {
                    GarminActivityId=processId,Kind="WritePreparation",
                    ActivityJson=processActivity,ActivitySha256=Hash(processActivity),
                    ExerciseSetsJson=processSets,SetsSha256=Hash(processSets),
                    CapturedUtc=DateTimeOffset.UtcNow
                };
                reopened.GarminSnapshots.Add(processBackup);
                await reopened.SaveChangesAsync();
                var processEntries=Enumerable.Range(0,2).Select(_=>new GarminWriteJournal {
                    GarminActivityId=processId,ApprovedSnapshotId=processBackup.Id,
                    FreshSnapshotId=processBackup.Id,Status="DryRunVerified",
                    OriginalSha256=Hash(processSets),CandidateSha256=Hash(processSets),
                    ProposedPayloadSha256=Hash(processSets),
                    RecoveryPayloadSha256=Hash(processSets),
                    CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow
                }).ToArray();
                reopened.WriteJournal.AddRange(processEntries);
                await reopened.SaveChangesAsync();
                reopened.ChangeTracker.Clear();
                await GarminProcessRaceTest.RunParentAsync(path,processId,
                    processEntries[0].Id,processEntries[1].Id);
                if(GarminWriteTransportAdapter.LiveWriteEnabled)
                    throw new InvalidOperationException("Live PUT must remain disabled.");
                var liveAdapterBlocked=false;
                try {
                    await GarminWriteTransportAdapter.SendAsync(crashId,crashOutgoing);
                }
                catch(InvalidOperationException) { liveAdapterBlocked=true; }
                if(!liveAdapterBlocked)
                    throw new InvalidOperationException("Adapter could issue live PUT.");
                Console.WriteLine("PASS: guarded Garmin transport refuses live PUT without mock.");
                foreach(var (mode, scenarioIndex) in new[] {"Verified","Mismatch","Timeout"}.Select((value,index)=>(value,index)))
                {
                    var id=activityId+20000+scenarioIndex;
                    var activityJson=JsonSerializer.Serialize(new {activityId=id});
                    var baseline=BuildSyntheticSets(id);
                    var candidate=GarminSetFieldDraft.Apply(baseline,
                        new[] {new SetFieldEdit(2,"repetitionCount",15,14)});
                    var outgoing=GarminTrialPayloadNormalizer.Normalize(candidate,id);
                    var backup=new GarminSnapshot {
                        GarminActivityId=id,Kind="WritePreparation",
                        ActivityJson=activityJson,ActivitySha256=Hash(activityJson),
                        ExerciseSetsJson=baseline,SetsSha256=Hash(baseline),
                        CapturedUtc=DateTimeOffset.UtcNow
                    };
                    reopened.GarminSnapshots.Add(backup);
                    await reopened.SaveChangesAsync();
                    var journal=new GarminWriteJournal {
                        GarminActivityId=id,ApprovedSnapshotId=backup.Id,
                        FreshSnapshotId=backup.Id,Status="DryRunVerified",
                        OriginalSha256=Hash(baseline),CandidateSha256=Hash(candidate),
                        ProposedPayloadSha256=Hash(outgoing),RecoveryPayloadSha256=Hash(baseline),
                        CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow
                    };
                    reopened.WriteJournal.Add(journal);
                    await reopened.SaveChangesAsync();
                    if(!await GarminTransactionDryRun.PersistSyntheticPayloadAsync(
                        reopened,id,journal.Id,candidate,outgoing))
                        throw new InvalidOperationException("Integration payload save failed.");
                    await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                        new(id,journal.Id,"Begin"),reopened),"Integrated Begin");
                    var observed=baseline;
                    var writes=0;
                    var result=await GarminTransactionDryRun.ExecuteVerifiedIsolationAsync(
                        reopened,id,journal.Id,
                        _=>Task.FromResult(new GarminTransactionDryRun.Observation(activityJson,observed)),
                        (_,payload)=>GarminWriteTransportAdapter.SendAsync(id,payload,(target,_)=>{
                            if(target!=id) throw new InvalidOperationException("Activity mismatch.");
                            writes++;
                            if(mode=="Timeout") throw new TimeoutException("Mock PUT timeout.");
                            observed=mode=="Verified" ? candidate :
                                GarminSetFieldDraft.Apply(baseline,
                                    new[] {new SetFieldEdit(2,"repetitionCount",15,13)});
                            return Task.CompletedTask;
                        }));
                    var expectedIntegrationStatus=mode=="Verified" ? "Verified" : "Uncertain";
                    if(result!=expectedIntegrationStatus || writes!=1)
                        throw new InvalidOperationException("Integrated "+mode+" outcome incorrect.");
                    reopened.ChangeTracker.Clear();
                    var persisted=await reopened.WriteJournal.AsNoTracking()
                        .SingleAsync(x=>x.Id==journal.Id);
                    var cache=await reopened.GarminSets.AsNoTracking()
                        .SingleOrDefaultAsync(x=>x.GarminActivityId==id);
                    if(persisted.Status!=expectedIntegrationStatus ||
                        (mode=="Verified" ? cache is null || cache.Sha256!=Hash(candidate)
                            : cache is not null))
                        throw new InvalidOperationException("Integrated "+mode+" cache/journal inconsistent.");
                    var duplicate=await GarminTransactionDryRun.ExecuteVerifiedIsolationAsync(
                        reopened,id,journal.Id,
                        _=>throw new InvalidOperationException("Unexpected repeated GET"),
                        (_,_)=>throw new InvalidOperationException("Unexpected repeated PUT"));
                    if(duplicate!="Blocked")
                        throw new InvalidOperationException("Integrated "+mode+" replay allowed.");
                    Console.WriteLine("PASS: integrated mocked PUT "+mode+
                        " -> "+expectedIntegrationStatus+"; journal/cache atomic and duplicate blocked.");
                }
                foreach(var scenario in new[] {"401","Timeout","NetworkError"})
                {
                    var physicalSends=0;
                    using var guard=new GarminSinglePutGuard(new FakeGarminTransport((request,ct)=>{
                        Interlocked.Increment(ref physicalSends);
                        if(scenario=="Timeout") throw new TaskCanceledException("Synthetic transport timeout.");
                        if(scenario=="NetworkError") throw new HttpRequestException("Synthetic network failure.");
                        return Task.FromResult(new System.Net.Http.HttpResponseMessage(
                            System.Net.HttpStatusCode.Unauthorized));
                    }));
                    using var http=new HttpClient(guard) {
                        BaseAddress=new Uri("https://synthetic.invalid")
                    };
                    var url="/activity-service/activity/123/exerciseSets";
                    try {
                        using var first=await http.PutAsync(url,new StringContent("{}"));
                    }
                    catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException) {}
                    var secondPutBlocked=false;
                    try {
                        using var retryResponse=await http.PutAsync(url,new StringContent("{}"));
                    }
                    catch(InvalidOperationException) {secondPutBlocked=true;}
                    if(!secondPutBlocked || physicalSends!=1 || guard.PutAttempts!=2)
                        throw new InvalidOperationException("Single PUT guard failed: "+scenario);
                    Console.WriteLine("PASS: HTTP guard "+scenario+" allows one physical PUT; retry blocked.");
                }
                foreach(var scenario in new[] {"401","Timeout","NetworkError"})
                {
                    var observedPhysicalPuts=0;
                    using var guardSdk=new GarminSinglePutGuard(new FakeGarminTransport((request,ct)=>{
                        if(request.Method!=HttpMethod.Put ||
                            request.RequestUri?.AbsolutePath!="/activity-service/activity/123/exerciseSets")
                            throw new InvalidOperationException("Unexpected SDK authentication HTTP request blocked.");
                        Interlocked.Increment(ref observedPhysicalPuts);
                        if(scenario=="Timeout") throw new TaskCanceledException("Synthetic timeout.");
                        if(scenario=="NetworkError") throw new HttpRequestException("Synthetic failure.");
                        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
                    }));
                    using var sdkHttp=new HttpClient(guardSdk);
                    var token=new global::Garmin.Connect.Auth.OAuth2Token {
                        AccessToken="synthetic-not-real",TokenType="Bearer",ExpiresIn=3600
                    };
                    var tokenCache=new FakeGarminTokenCache(token);
                    var sdkContext=new global::Garmin.Connect.GarminConnectContext(
                        sdkHttp,new global::Garmin.Connect.Auth.BasicAuthParameters(
                            "synthetic@example.invalid","synthetic-password"),
                        new global::Garmin.Connect.Auth.NotImplementedMfaCode(),tokenCache);
                    try
                    {
                        using var fakePayload=JsonDocument.Parse("{}");
                        await sdkContext.MakeHttpPut(
                            "/activity-service/activity/123/exerciseSets",fakePayload.RootElement);
                    }
                    catch(Exception) { /* expected: 401 auth refresh is locally refused; other scenarios fail */ }
                    if(observedPhysicalPuts!=1 || guardSdk.PutAttempts!=1)
                        throw new InvalidOperationException("SDK mock made unexpected physical PUT count: "+scenario);
                    Console.WriteLine("PASS: GarminConnectContext "+scenario+
                        " -> one physical PUT, no external HTTP.");
                }
                // SDK 401 -> successful mocked OAuth1/OAuth2 exchange -> SDK retries PUT.
                // The physical write guard must intercept the retried request.
                {
                    var physicalPuts=0;
                    var refreshPosts=0;
                    using var guardedSdk=new GarminSinglePutGuard(
                        new FakeGarminTransport((request,ct)=>{
                            if(request.Method==HttpMethod.Put &&
                                request.RequestUri?.AbsolutePath.EndsWith("/exerciseSets")==true)
                            {
                                physicalPuts++;
                                return Task.FromResult(new HttpResponseMessage(
                                    System.Net.HttpStatusCode.Unauthorized));
                            }
                            if(request.Method==HttpMethod.Post &&
                                request.RequestUri?.AbsolutePath==
                                    "/oauth-service/oauth/exchange/user/2.0")
                            {
                                refreshPosts++;
                                return Task.FromResult(new HttpResponseMessage(
                                    System.Net.HttpStatusCode.OK) {
                                    Content=new StringContent("""
                                        {"access_token":"synthetic-refreshed","token_type":"Bearer","expires_in":3600}
                                        """)
                                });
                            }
                            throw new InvalidOperationException("Unexpected outbound request.");
                        }));
                    using var sdkHttp=new HttpClient(guardedSdk);
                    var oauthCache=new FakeGarminTokenCache(
                        new global::Garmin.Connect.Auth.OAuth2Token {
                            AccessToken="synthetic-old",TokenType="Bearer",ExpiresIn=3600
                        },new global::Garmin.Connect.Auth.OAuth1Token {
                            Token="synthetic-oauth1",TokenSecret="synthetic-secret"
                        });
                    var sdk=new global::Garmin.Connect.GarminConnectContext(
                        sdkHttp,new global::Garmin.Connect.Auth.BasicAuthParameters(
                            "synthetic@example.invalid","synthetic-password"),
                        new global::Garmin.Connect.Auth.NotImplementedMfaCode(),oauthCache);
                    var guardRejected=false;
                    try
                    {
                        using var body=JsonDocument.Parse("{}");
                        await sdk.MakeHttpPut(
                            "/activity-service/activity/123/exerciseSets",body.RootElement);
                    }
                    catch(InvalidOperationException ex) when(
                        ex.Message.Contains("Blocked repeated Garmin exerciseSets PUT."))
                    {
                        guardRejected=true;
                    }
                    if(!guardRejected || physicalPuts!=1 || refreshPosts!=1 ||
                        guardedSdk.PutAttempts!=2 || oauthCache.RefreshCount!=1)
                        throw new InvalidOperationException(
                            "OAuth refresh retry was not intercepted by the guarded SDK.");
                    Console.WriteLine("PASS: SDK 401 -> successful OAuth refresh -> second PUT blocked; one physical PUT.");
                }
                // Factory integration: the actual SDK is always constructed with
                // a per-attempt guard. Repeated SDK PUT must not hit transport.
                {
                    var sendsThroughFactory=0;
                    using var scope=GarminGuardedWriteContextFactory.Create(
                        new FakeGarminTransport((request,ct)=>{
                            sendsThroughFactory++;
                            return Task.FromResult(new HttpResponseMessage(
                                System.Net.HttpStatusCode.OK));
                        }),
                        new global::Garmin.Connect.Auth.BasicAuthParameters(
                            "synthetic@example.invalid","synthetic-password"),
                        new global::Garmin.Connect.Auth.NotImplementedMfaCode(),
                        new FakeGarminTokenCache(new global::Garmin.Connect.Auth.OAuth2Token {
                            AccessToken="synthetic",TokenType="Bearer",ExpiresIn=3600
                        }));
                    using(var body=JsonDocument.Parse("{}"))
                    using(var response=await scope.Context.MakeHttpPut(
                        "/activity-service/activity/123/exerciseSets",body.RootElement))
                    {
                        if(!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("First factory PUT did not succeed.");
                    }
                    var refusedByFactory=false;
                    try
                    {
                        using var body=JsonDocument.Parse("{}");
                        using var response=await scope.Context.MakeHttpPut(
                            "/activity-service/activity/123/exerciseSets",body.RootElement);
                    }
                    catch(InvalidOperationException ex) when(
                        ex.Message.Contains("Blocked repeated Garmin exerciseSets PUT."))
                    {
                        refusedByFactory=true;
                    }
                    if(!refusedByFactory || sendsThroughFactory!=1 || scope.Guard.PutAttempts!=2)
                        throw new InvalidOperationException("Factory allowed a duplicate PUT.");
                    Console.WriteLine("PASS: guarded write context factory allows one SDK PUT; repeat blocked.");
                }
                foreach(var (scenario,index) in new[] {"Verified","Mismatch","Timeout"}
                    .Select((value,i)=>(value,i)))
                {
                    var id=activityId+25000+index;
                    var originalActivity=JsonSerializer.Serialize(new {activityId=id});
                    var originalSets=BuildSyntheticSets(id);
                    var candidate=GarminSetFieldDraft.Apply(originalSets,
                        new[] {new SetFieldEdit(2,"repetitionCount",15,14)});
                    var outgoing=GarminTrialPayloadNormalizer.Normalize(candidate,id);
                    var snapshot=new GarminSnapshot {
                        GarminActivityId=id,Kind="WritePreparation",
                        ActivityJson=originalActivity,ActivitySha256=Hash(originalActivity),
                        ExerciseSetsJson=originalSets,SetsSha256=Hash(originalSets),
                        CapturedUtc=DateTimeOffset.UtcNow
                    };
                    reopened.GarminSnapshots.Add(snapshot);
                    await reopened.SaveChangesAsync();
                    var journal=new GarminWriteJournal {
                        GarminActivityId=id,ApprovedSnapshotId=snapshot.Id,
                        FreshSnapshotId=snapshot.Id,Status="DryRunVerified",
                        OriginalSha256=Hash(originalSets),CandidateSha256=Hash(candidate),
                        ProposedPayloadSha256=Hash(outgoing),
                        RecoveryPayloadSha256=Hash(originalSets),
                        CreatedUtc=DateTimeOffset.UtcNow,UpdatedUtc=DateTimeOffset.UtcNow
                    };
                    reopened.WriteJournal.Add(journal);
                    await reopened.SaveChangesAsync();
                    if(!await GarminTransactionDryRun.PersistSyntheticPayloadAsync(
                        reopened,id,journal.Id,candidate,outgoing))
                        throw new InvalidOperationException("Unified payload persistence failed.");
                    await RequireSuccess(GarminJournalLifecycle.TransitionAsync(
                        new(id,journal.Id,"Begin"),reopened),"Unified Begin");
                    var observedSets=originalSets;
                    var physicalPuts=0;
                    var reads=0;
                    using var scope=GarminGuardedWriteContextFactory.CreateIsolatedTest(
                        new FakeGarminTransport((request,ct)=>{
                            var expectedPath=$"/activity-service/activity/{id}";
                            if(request.Method==HttpMethod.Get &&
                                request.RequestUri?.AbsolutePath==expectedPath)
                            {
                                reads++;
                                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                                    Content=new StringContent(originalActivity)
                                });
                            }
                            if(request.Method==HttpMethod.Get &&
                                request.RequestUri?.AbsolutePath==expectedPath+"/exerciseSets")
                            {
                                reads++;
                                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                                    Content=new StringContent(observedSets)
                                });
                            }
                            if(request.Method==HttpMethod.Put &&
                                request.RequestUri?.AbsolutePath==expectedPath+"/exerciseSets")
                            {
                                physicalPuts++;
                                if(scenario=="Timeout") throw new TaskCanceledException("Synthetic timeout");
                                observedSets=scenario=="Verified" ? candidate :
                                    GarminSetFieldDraft.Apply(originalSets,
                                        new[] {new SetFieldEdit(2,"repetitionCount",15,13)});
                                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
                            }
                            throw new InvalidOperationException("Unexpected synthetic SDK request.");
                        }),
                        new global::Garmin.Connect.Auth.BasicAuthParameters(
                            "synthetic@example.invalid","synthetic-password"),
                        new global::Garmin.Connect.Auth.NotImplementedMfaCode(),
                        new FakeGarminTokenCache(new global::Garmin.Connect.Auth.OAuth2Token {
                            AccessToken="synthetic",TokenType="Bearer",ExpiresIn=3600
                        }));
                    var status=await GarminUnifiedWriteWorkflow.ExecuteIsolatedAsync(
                        reopened,id,journal.Id,scope);
                    var expectedStatus=scenario=="Verified" ? "Verified" : "Uncertain";
                    reopened.ChangeTracker.Clear();
                    var finalJournal=await reopened.WriteJournal.AsNoTracking()
                        .SingleAsync(x=>x.Id==journal.Id);
                    var finalCache=await reopened.GarminSets.AsNoTracking()
                        .SingleOrDefaultAsync(x=>x.GarminActivityId==id);
                    if(status!=expectedStatus || finalJournal.Status!=expectedStatus ||
                        physicalPuts!=1 || scope.Guard.PutAttempts!=1 || reads<2 ||
                        (scenario=="Verified"
                            ? finalCache is null || finalCache.Sha256!=Hash(candidate)
                            : finalCache is not null))
                        throw new InvalidOperationException("Unified SDK workflow mismatch: "+scenario);
                    var repeat=await GarminUnifiedWriteWorkflow.ExecuteIsolatedAsync(
                        reopened,id,journal.Id,scope);
                    if(repeat!="Blocked" || physicalPuts!=1)
                        throw new InvalidOperationException("Unified SDK workflow replay allowed.");
                    Console.WriteLine("PASS: unified guarded SDK "+scenario+
                        " -> "+expectedStatus+"; journal/cache consistent; no replay.");
                }
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

    private sealed class FakeGarminTokenCache(global::Garmin.Connect.Auth.OAuth2Token token,
        global::Garmin.Connect.Auth.OAuth1Token? oauth1 = null)
        : global::Garmin.Connect.Auth.ITokenCache
    {
        public int RefreshCount { get; private set; }
        public Task<global::Garmin.Connect.Auth.OAuth2Token?> GetOAuth2Token(
            CancellationToken ct) => Task.FromResult<global::Garmin.Connect.Auth.OAuth2Token?>(token);
        public Task SetOAuth2Token(global::Garmin.Connect.Auth.OAuth2Token value,
            CancellationToken ct) { RefreshCount++; return Task.CompletedTask; }
        public Task<global::Garmin.Connect.Auth.OAuth1Token?> GetOAuth1Token(
            CancellationToken ct) => Task.FromResult(oauth1);
        public Task SetOAuth1Token(global::Garmin.Connect.Auth.OAuth1Token value,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGarminTransport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => send(request,ct);
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
