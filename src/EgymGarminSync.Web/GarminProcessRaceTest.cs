using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Real OS child processes, disposable SQLite only. No HTTP or Garmin client.</summary>
internal static class GarminProcessRaceTest
{
    internal static async Task<int> ClaimChildAsync(string dbPath, long activityId, long journalId,
        string startSignalPath)
    {
        try
        {
            if (!Path.GetFullPath(dbPath).StartsWith(Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Child refuses a database outside temp.");
            var options=new DbContextOptionsBuilder<SyncDbContext>()
                .UseSqlite("Data Source="+dbPath).Options;
            var wait=Stopwatch.StartNew();
            while(!File.Exists(startSignalPath))
            {
                if(wait.Elapsed>TimeSpan.FromSeconds(20)) return 3;
                await Task.Delay(15);
            }
            await using var db=new SyncDbContext(options);
            var result=await GarminJournalLifecycle.TransitionAsync(
                new(activityId,journalId,"Begin"),db);
            return result is IStatusCodeHttpResult {StatusCode:200} ? 0 : 2;
        }
        catch(DbUpdateException) { return 2; }
        catch(Microsoft.Data.Sqlite.SqliteException ex) when(ex.SqliteErrorCode is 5 or 19) { return 2; }
        catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    }

    internal static async Task RunParentAsync(string path, long activityId,
        long journalA, long journalB)
    {
        var dll=typeof(GarminProcessRaceTest).Assembly.Location;
        var signal=Path.Combine(Path.GetTempPath(),"egym-process-race-"+Guid.NewGuid().ToString("N")+".go");
        Process Spawn(long journal)
        {
            var info=new ProcessStartInfo("dotnet") {
                UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true
            };
            foreach(var argument in new[] {dll,"--test-process-claim-child",path,
                activityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                journal.ToString(System.Globalization.CultureInfo.InvariantCulture),signal})
                info.ArgumentList.Add(argument);
            return Process.Start(info) ?? throw new InvalidOperationException("Cannot spawn child.");
        }
        using var left=Spawn(journalA);
        using var right=Spawn(journalB);
        try
        {
            await Task.Delay(350);
            File.WriteAllText(signal,"start");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await Task.WhenAll(left.WaitForExitAsync(timeout.Token),
                right.WaitForExitAsync(timeout.Token));
            var winners=new[] {left.ExitCode,right.ExitCode};
            if(winners.Count(x=>x==0)!=1 || winners.Count(x=>x==2)!=1)
                throw new InvalidOperationException("Unexpected child results: "+
                    string.Join(",",winners)+"; "+
                    await left.StandardError.ReadToEndAsync()+"; "+
                    await right.StandardError.ReadToEndAsync());
            var options=new DbContextOptionsBuilder<SyncDbContext>()
                .UseSqlite("Data Source="+path).Options;
            await using var check=new SyncDbContext(options);
            if(await check.WriteJournal.AsNoTracking().CountAsync(x=>
                x.GarminActivityId==activityId && x.Status=="Ready")!=1)
                throw new InvalidOperationException("Process race persisted multiple owners.");
            Console.WriteLine("PASS: separate .NET OS processes race; exactly one Ready journal.");
        }
        finally
        {
            if(!left.HasExited) left.Kill(entireProcessTree:true);
            if(!right.HasExited) right.Kill(entireProcessTree:true);
            try {File.Delete(signal);}catch(IOException){}
        }
    }
}
