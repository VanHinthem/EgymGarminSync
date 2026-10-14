using EgymGarminSync.Egym;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5187");
builder.Services.AddCors(options => options.AddPolicy("LocalUi", policy =>
    policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors("LocalUi");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", writeEnabled = false }));

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
            string? completed = w.TryGetProperty("completedAt", out var dt) ? dt.ToString() : null;
            var exerciseCount = w.TryGetProperty("exercises", out var exercises) &&
                exercises.ValueKind == JsonValueKind.Array ? exercises.GetArrayLength() : 0;
            return new { id = $"egym-{i}", completedAt = completed, exerciseCount };
        }).ToArray();
        return Results.Ok(sessions);
    }
    catch (Exception)
    {
        // Do not expose credentials or upstream response bodies.
        return Results.Problem("Unable to retrieve EGYM sessions; check server configuration and upstream access.", statusCode: 502);
    }
});

app.MapGet("/api/garmin/sessions", () => Results.Problem(
    "Garmin session reading is not connected to the web host yet. CLI token cache remains separate.",
    statusCode: 501));

app.MapPost("/api/sync", () => Results.Problem(
    "Garmin writes are disabled in this first web slice.", statusCode: 501));

app.Run();
