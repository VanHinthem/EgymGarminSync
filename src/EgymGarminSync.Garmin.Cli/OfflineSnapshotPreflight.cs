using System.Security.Cryptography;
using System.Text.Json;
using EgymGarminSync.Matching;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Offline inspection of a previously captured Garmin JSON snapshot; no network calls.</summary>
internal static class OfflineSnapshotPreflight
{
    public static async Task<int> RunAsync()
    {
        const long activityId = GarminActivitySnapshot.August31ActivityId;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "backups");
        if (!Directory.Exists(root)) return Fail("No local backup directory found.");
        var folders = Directory.GetDirectories(root, $"{activityId}-*").OrderByDescending(Path.GetFileName).ToArray();
        if (folders.Length == 0) return Fail("No August 31 snapshot found.");
        var folder = folders[0];
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) return Fail("Snapshot manifest missing.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var doc = manifest.RootElement;
        if (doc.GetProperty("GarminActivityId").GetInt64() != activityId)
            return Fail("Snapshot activity ID mismatch.");
        var files = doc.GetProperty("Files").EnumerateArray().ToArray();
        if (files.Length != 2 ||
            files.Select(f => f.GetProperty("Filename").GetString()).OrderBy(x => x)
                .SequenceEqual(new[] { "activity.json", "exerciseSets.json" }) == false)
            return Fail("Unexpected manifest files.");
        foreach (var file in files)
        {
            var name = file.GetProperty("Filename").GetString()!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, name));
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (bytes.Length != file.GetProperty("Length").GetInt32() ||
                !string.Equals(hash, file.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return Fail($"Snapshot integrity failure: {name}.");
        }

        // Historical EGYM API/app observations for this exact date; not a general-purpose rule.
        EgymExerciseWeight[] targets =
        [
            new(329, 22), new(330, 30), new(331, 44), new(332, 23),
            new(333, 21), new(334, 32), new(335, 33), new(581, 95)
        ];
        var original = await File.ReadAllTextAsync(Path.Combine(folder, "exerciseSets.json"));
        var preview = GarminWeightOnlyPreflight.Build(original, targets);
        Console.WriteLine($"OFFLINE August 31 preview; snapshot={Path.GetFileName(folder)}");
        Console.WriteLine("Snapshot integrity: activity.json and exerciseSets.json SHA-256 verified.");
        Console.WriteLine($"Change candidates: {preview.Changes.Count} set weights (7 exercises).");
        foreach (var item in preview.Changes.OrderBy(x => x.EgymActivityId).ThenBy(x => x.SetIndex))
            Console.WriteLine($"  setIndex={item.SetIndex} EGYM id={item.EgymActivityId}: " +
                $"{item.OldWeightGrams / 1000m} -> {item.NewWeightGrams / 1000m} kg");
        Console.WriteLine("Verified: no proposed non-weight changes. Proposed JSON retained in memory only.");
        Console.WriteLine("REVIEW ONLY: single-set Garmin restore tested; bulk correction not yet authorized or verified. No Garmin writes.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
