using EgymGarminSync.Egym;
using EgymGarminSync.Web;
using System.Globalization;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;

if (args.Contains("--test-uncertain-isolation", StringComparer.Ordinal))
{
    Environment.ExitCode = await GarminUncertainIsolationTest.RunAsync();
    return;
}

if (args.Length==5 && args[0]=="--test-process-claim-child")
{
    Environment.ExitCode=await GarminProcessRaceTest.ClaimChildAsync(
        args[1],long.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture),
        long.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture),args[4]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5187");
builder.Services.AddCors(options => options.AddPolicy("LocalUi", policy =>
    policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "EgymGarminSync", "sync.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<SyncDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await database.Database.EnsureCreatedAsync();
    // Existing SQLite files predate the new sets cache. Add this table without
    // dropping or recreating the user's previously imported session data.
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "GarminSets" (
            "GarminActivityId" INTEGER NOT NULL PRIMARY KEY,
            "ExerciseSetsJson" TEXT NOT NULL,
            "Sha256" TEXT NOT NULL,
            "FetchedUtc" TEXT NOT NULL
        )
        """);
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "WriteJournal" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "GarminActivityId" INTEGER NOT NULL,
            "ApprovedSnapshotId" INTEGER NOT NULL,
            "FreshSnapshotId" INTEGER NOT NULL,
            "Status" TEXT NOT NULL,
            "OriginalSha256" TEXT NOT NULL,
            "CandidateSha256" TEXT NOT NULL,
            "ProposedPayloadSha256" TEXT NOT NULL,
            "RecoveryPayloadSha256" TEXT NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL,
            "ErrorCode" TEXT NULL,
            "OperationId" TEXT NULL
        )
        """);
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "WritePayloads" (
            "JournalId" INTEGER NOT NULL PRIMARY KEY,
            "GarminActivityId" INTEGER NOT NULL,
            "CandidateJson" TEXT NOT NULL,
            "OutgoingJson" TEXT NOT NULL,
            "CandidateSha256" TEXT NOT NULL,
            "OutgoingSha256" TEXT NOT NULL
        )
        """);
    await database.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_WriteJournal_ActiveActivity"
        ON "WriteJournal" ("GarminActivityId")
        WHERE "Status" IN ('Preparing', 'Ready', 'WriteStarted', 'Uncertain')
        """);
    // Non-destructive schema evolution for existing local sync.db files.
    var writeJournalColumns=await database.Database.SqlQueryRaw<string>(
        "SELECT name AS Value FROM pragma_table_info('WriteJournal')").ToListAsync();
    if(!writeJournalColumns.Contains("OperationId",StringComparer.OrdinalIgnoreCase))
        await database.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"WriteJournal\" ADD COLUMN \"OperationId\" TEXT NULL");
    await GarminCrashRecovery.RecoverAsync(database);
}
app.UseCors("LocalUi");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", writeEnabled = false, database = "SQLite" }));
app.MapPost("/api/cache/import-initial", (SyncDbContext db) => InitialCacheImport.ImportAsync(db));
app.MapPost("/api/cache/sync", (SyncDbContext db) => InitialCacheImport.ImportAsync(db, incremental: true));
app.MapGet("/api/cache/sessions", async (string? source, SyncDbContext db) =>
{
    if (source is not ("EGYM" or "Garmin"))
        return Results.BadRequest(new { error = "Specify source=EGYM or source=Garmin." });
    var stored = await db.Sessions.AsNoTracking().Where(s => s.Source == source).ToListAsync();
    var items = stored.Select(s => {
        string? method = null;
        if (s.Source == "EGYM")
        {
            using var doc = JsonDocument.Parse(s.SourceJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("exercises", out var exercises) && exercises.ValueKind == JsonValueKind.Array)
            {
                var methods = exercises.EnumerateArray()
                    .Where(e => e.TryGetProperty("sets", out var sets) && sets.ValueKind == JsonValueKind.Array)
                    .SelectMany(e => e.GetProperty("sets").EnumerateArray())
                    .Where(set => set.TryGetProperty("trainingMethod", out var tm) && tm.ValueKind == JsonValueKind.String)
                    .Select(set => set.GetProperty("trainingMethod").GetString()).Distinct().ToArray();
                if (methods.Length == 1) method = methods[0];
            }
        }
        return new { id=s.SourceId, completedAt=s.StartUtc, exerciseCount=s.ExerciseCount,
            name=s.Name, fetchedUtc=s.FetchedUtc, method };
    }).OrderByDescending(x => x.completedAt).ToArray();
    return Results.Ok(items);
});
app.MapGet("/api/cache/status", async (SyncDbContext db) => Results.Ok(new {
    sessions = await db.Sessions.CountAsync(),
    egym = await db.Sessions.CountAsync(s => s.Source == "EGYM"),
    garmin = await db.Sessions.CountAsync(s => s.Source == "Garmin"),
    checkpoints = await db.Checkpoints.AsNoTracking().ToArrayAsync(),
    snapshots = await db.GarminSnapshots.CountAsync(),
    cachedGarminSets = await db.GarminSets.CountAsync(),
    writeJournalEntries = await db.WriteJournal.CountAsync()
}));

app.MapGet("/api/egym/sessions", async (int? days) =>
{
    var period = Math.Clamp(days ?? 180, 1, 365);
    var email = Environment.GetEnvironmentVariable("EGYM_EMAIL");
    var password = Environment.GetEnvironmentVariable("EGYM_PASSWORD");
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Problem("EGYM_EMAIL and EGYM_PASSWORD must be configured on the server.", statusCode: 503);
    try
    {
        using var client = new EgymClient(Environment.GetEnvironmentVariable("EGYM_BASE_URL"), email, password);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var workouts = await client.GetWorkoutsAsync(today.AddDays(-period), today);
        var sessions = workouts.Select((w, i) =>
        {
            string? completed = null;
            if (w.TryGetProperty("completedAt", out var dt) &&
                w.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tz.GetString()!);
                var local = DateTime.SpecifyKind(DateTime.Parse(dt.GetString()!,
                    CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
                if (!zone.IsAmbiguousTime(local) && !zone.IsInvalidTime(local))
                    completed = TimeZoneInfo.ConvertTimeToUtc(local, zone).ToString("O");
            }
            var exerciseCount = w.TryGetProperty("exercises", out var exercises) &&
                exercises.ValueKind == JsonValueKind.Array ? exercises.GetArrayLength() : 0;
            var methods = w.GetProperty("exercises").EnumerateArray()
                .Where(e => e.TryGetProperty("sets", out var sets) && sets.ValueKind == JsonValueKind.Array)
                .SelectMany(e => e.GetProperty("sets").EnumerateArray())
                .Where(set => set.TryGetProperty("trainingMethod", out var method) && method.ValueKind == JsonValueKind.String)
                .Select(set => set.GetProperty("trainingMethod").GetString())
                .Where(method => !string.IsNullOrWhiteSpace(method)).Distinct().ToArray();
            var methodName = methods.Length == 1 ? methods[0] : null;
            return new { id = $"egym-{i}", completedAt = completed, exerciseCount, method = methodName };
        }).ToArray();
        return Results.Ok(sessions);
    }
    catch (Exception)
    {
        // Do not expose credentials or upstream response bodies.
        return Results.Problem("Unable to retrieve EGYM sessions; check server configuration and upstream access.", statusCode: 502);
    }
});

app.MapGet("/api/garmin/sessions", GarminReadOnlySessions.GetAsync);
app.MapPost("/api/cache/garmin-sets/import", GarminSetsImport.ImportAsync);
app.MapGet("/api/cache/garmin-sets/{activityId:long}", GarminSetsImport.GetCachedAsync);
app.MapGet("/api/compare", ReadOnlySetComparison.GetAsync);
app.MapGet("/api/cache/compare", async (string egymId, long garminId, SyncDbContext db) =>
{
    var egym = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s =>
        s.Source == "EGYM" && s.SourceId == egymId);
    var garmin = await db.GarminSets.AsNoTracking().SingleOrDefaultAsync(s =>
        s.GarminActivityId == garminId);
    if (egym is null || garmin is null)
        return Results.NotFound(new { error = "Missing cached EGYM workout or Garmin sets." });
    using var source = JsonDocument.Parse(egym.SourceJson);
    using var target = JsonDocument.Parse(garmin.ExerciseSetsJson);
    return ReadOnlySetComparison.CompareCached(source.RootElement, target.RootElement);
});
app.MapPost("/api/sync/preview", GarminEditPreview.PreviewAsync);
app.MapPost("/api/sync/prepare", GarminEditPreview.PrepareAsync);
app.MapGet("/api/sync/restore-plan/{activityId:long}", GarminRestorePlan.GetAsync);
app.MapPost("/api/sync/rehearse", GarminWriteRehearsal.RehearseAsync);
app.MapPost("/api/sync/save-plan", GarminSavePlan.PlanAsync);
app.MapPost("/api/sync/save-prepare", GarminSaveOrchestrator.PrepareAsync);
app.MapGet("/api/sync/write-journal/{activityId:long}", GarminWriteJournalRead.GetAsync);
app.MapPost("/api/sync/write-simulate", GarminWriteSimulation.RunAsync);
app.MapPost("/api/sync/journal-transition-test", GarminJournalLifecycle.TransitionAsync);
app.MapPost("/api/sync/reconcile-simulate", GarminReconciliation.SimulateAsync);
app.MapPost("/api/sync/reconcile-live-readonly", GarminLiveReconciliation.ReadAsync);

app.MapPost("/api/sync", () => Results.Problem(
    "Garmin writes are disabled in this first web slice.", statusCode: 501));

app.Run();
