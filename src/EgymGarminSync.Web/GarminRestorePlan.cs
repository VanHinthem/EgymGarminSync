using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>Read-only backup verification and restoration planning. Never issues Garmin writes.</summary>
internal static class GarminRestorePlan
{
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async Task<IResult> GetAsync(long activityId, SyncDbContext db)
    {
        if (activityId <= 0) return Results.BadRequest();
        var snapshots = (await db.GarminSnapshots.AsNoTracking()
            .Where(s => s.GarminActivityId == activityId)
            .ToListAsync())
            .OrderByDescending(s => s.CapturedUtc)
            .ToList();
        if (snapshots.Count == 0) return Results.NotFound();
        var cached = await db.GarminSets.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GarminActivityId == activityId);
        var items = snapshots.Select(s => new {
            id = s.Id, kind = s.Kind, capturedUtc = s.CapturedUtc,
            sha256 = s.SetsSha256,
            integrityOk = Hash(s.ExerciseSetsJson) == s.SetsSha256 &&
                Hash(s.ActivityJson) == s.ActivitySha256,
            matchesCachedSets = cached is not null && cached.Sha256 == s.SetsSha256
        }).ToArray();
        return Results.Ok(new {
            activityId, snapshots = items, canRestore = false,
            warning = "Planning only. Restoring requires a fresh Garmin GET, validated full replace-all payload, explicit confirmation and readback. No PUT endpoint is enabled."
        });
    }
}
