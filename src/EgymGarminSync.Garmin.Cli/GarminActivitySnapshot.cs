using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Garmin.Connect.Auth;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>GET-only local JSON snapshot, not a proven restorable Garmin backup.</summary>
internal static class GarminActivitySnapshot
{
    internal const long August31ActivityId = 24180037460L;

    public static async Task<int> RunAsync(GarminConnectContext context)
    {
        const long id = August31ActivityId;
        // Avoid overwriting an existing capture and never save sensitive data in the repository.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "backups");
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, $"{id}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var captured = new List<SnapshotFile>();
            foreach (var (filename, endpoint) in new[]
            {
                ("activity.json", $"/activity-service/activity/{id}"),
                ("exerciseSets.json", $"/activity-service/activity/{id}/exerciseSets")
            })
            {
                var result = await context.GetAndDeserialize<JsonElement>(endpoint);
                if (result.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"Unexpected response shape for {filename}.");
                if (filename == "exerciseSets.json" &&
                    (!result.TryGetProperty("exerciseSets", out var sets) || sets.ValueKind != JsonValueKind.Array))
                    throw new InvalidOperationException("Missing exerciseSets; snapshot aborted.");

                // API adapter exposes a deserialized JsonElement: this preserves JSON values but
                // is NOT a byte-identical archival copy of the original HTTP response.
                var bytes = Encoding.UTF8.GetBytes(result.GetRawText());
                var file = Path.Combine(folder, filename);
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await stream.WriteAsync(bytes);
                var expected = Convert.ToHexString(SHA256.HashData(bytes));
                var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    throw new IOException($"SHA-256 mismatch for {filename}.");
                captured.Add(new SnapshotFile(filename, bytes.Length, expected));
            }

            var manifest = new SnapshotManifest(id, DateTimeOffset.UtcNow,
                "JSON values captured via GET and JsonElement; HTTP raw bytes and FIT are NOT included; restoration NOT validated",
                captured);
            var manifestPath = Path.Combine(folder, "manifest.json");
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Console.WriteLine($"Local GET-only Garmin snapshot saved: {folder}");
            foreach (var file in captured)
                Console.WriteLine($"  {file.Filename}: {file.Length} bytes, SHA-256={file.Sha256}");
            Console.WriteLine("Snapshot is NOT a tested restoration method. No Garmin changes were made.");
            return 0;
        }
        catch
        {
            // Do not leave a partial capture that could be mistaken for a valid backup.
            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    private sealed record SnapshotFile(string Filename, int Length, string Sha256);
    private sealed record SnapshotManifest(long GarminActivityId, DateTimeOffset CapturedUtc,
        string Limitations, IReadOnlyList<SnapshotFile> Files);
}
