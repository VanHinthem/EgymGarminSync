using EgymGarminSync.Egym;
using EgymGarminSync.Web;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

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
    await scope.ServiceProvider.GetRequiredService<SyncDbContext>().Database.EnsureCreatedAsync();
app.UseCors("LocalUi");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", writeEnabled = false, database = "SQLite" }));
app.MapGet("/api/cache/status", async (SyncDbContext db) => Results.Ok(new {
    sessions = await db.Sessions.CountAsync(),
    egym = await db.Sessions.CountAsync(s => s.Source == "EGYM"),
    garmin = await db.Sessions.CountAsync(s => s.Source == "Garmin"),
    checkpoints = await db.Checkpoints.AsNoTracking().ToArrayAsync(),
    snapshots = await db.GarminSnapshots.CountAsync()
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
app.MapGet("/api/compare", ReadOnlySetComparison.GetAsync);
app.MapPost("/api/sync/preview", GarminEditPreview.PreviewAsync);

app.MapPost("/api/sync", () => Results.Problem(
    "Garmin writes are disabled in this first web slice.", statusCode: 501));

app.Run();
