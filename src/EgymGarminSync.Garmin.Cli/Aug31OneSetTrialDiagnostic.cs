using System.Security.Cryptography;
using System.Text.Json;
using EgymGarminSync.Matching;
using Garmin.Connect;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Read-only live freshness check plus one-set payload preparation, never sends PUT.</summary>
internal static class Aug31OneSetTrialDiagnostic
{
    public static async Task<int> RunAsync(GarminConnectContext context)
    {
        const long id = GarminActivitySnapshot.August31ActivityId;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "backups");
        if (!Directory.Exists(root)) return Refuse("No snapshot directory.");
        var dirs = Directory.GetDirectories(root, $"{id}-*").OrderByDescending(Path.GetFileName).ToArray();
        if (dirs.Length == 0) return Refuse("No Aug31 snapshot.");
        var folder = dirs[0];
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) return Refuse("Snapshot manifest missing.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var m = manifest.RootElement;
        if (m.GetProperty("GarminActivityId").GetInt64() != id) return Refuse("Manifest activity mismatch.");
        var files = m.GetProperty("Files").EnumerateArray().ToArray();
        if (files.Length != 2) return Refuse("Unexpected manifest file count.");
        foreach (var entry in files)
        {
            var filename = entry.GetProperty("Filename").GetString();
            if (filename is not ("activity.json" or "exerciseSets.json"))
                return Refuse("Unexpected manifest entry.");
            var path = Path.Combine(folder, filename);
            if (!File.Exists(path)) return Refuse("Snapshot file missing.");
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length != entry.GetProperty("Length").GetInt32() ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                    entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return Refuse("Snapshot SHA-256 mismatch.");
        }
        if (files.Select(e => e.GetProperty("Filename").GetString()).Distinct().Count() != 2)
            return Refuse("Duplicate manifest entry.");

        var savedSets = await File.ReadAllTextAsync(Path.Combine(folder, "exerciseSets.json"));
        var savedActivity = await File.ReadAllTextAsync(Path.Combine(folder, "activity.json"));
        var currentActivity = await context.GetAndDeserialize<JsonElement>($"/activity-service/activity/{id}");
        using var savedActivityDoc = JsonDocument.Parse(savedActivity);
        if (!JsonElement.DeepEquals(savedActivityDoc.RootElement, currentActivity))
            return Refuse("Garmin activity summary/metadata diverges from saved snapshot.");
        var currentSets = await context.GetAndDeserialize<JsonElement>($"/activity-service/activity/{id}/exerciseSets");
        try
        {
            var preview = OneSetTrialPlanner.Prepare(savedSets, currentSets.GetRawText());
            var change = preview.Changes.Single();
            Console.WriteLine($"READ-ONLY Aug31 live freshness check passed: activity={id}, 36 saved sets match Garmin.");
            Console.WriteLine($"ONE SET ONLY: Chest Press setIndex={change.SetIndex}: {change.OldWeightGrams / 1000m} -> {change.NewWeightGrams / 1000m} kg.");
            Console.WriteLine("Proposed replacement JSON built in memory; all other JSON fields verified equal.");
            Console.WriteLine("NO PUT/POST, no restore, no Garmin writes. Update contract still requires validation.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            return Refuse(ex.Message);
        }
    }

    private static int Refuse(string reason)
    {
        Console.Error.WriteLine($"One-set trial blocked: {reason}");
        return 1;
    }
}
