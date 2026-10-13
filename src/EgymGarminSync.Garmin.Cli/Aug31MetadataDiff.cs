using System.Security.Cryptography;
using System.Text.Json;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>GET-only, bounded metadata path diff; never prints account-related values.</summary>
internal static class Aug31MetadataDiff
{
    public static async Task<int> RunAsync(GarminConnectContext context)
    {
        const long id = GarminActivitySnapshot.August31ActivityId;
        var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "backups");
        const string originalHash = "4DF6E4DC588F297D199C4870650730B5BB51DCAB89B44FF2EA12CB6BDE91268A";
        var snapshot = Directory.Exists(backupRoot)
            ? Directory.GetDirectories(backupRoot, $"{id}-*")
                .Select(x => Path.Combine(x, "activity.json"))
                .FirstOrDefault(x => File.Exists(x) &&
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))) == originalHash)
            : null;
        if (snapshot is null) { Console.Error.WriteLine("Original activity snapshot missing."); return 1; }
        using var original = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot));
        var live = await context.GetAndDeserialize<JsonElement>($"/activity-service/activity/{id}");
        var changes = new List<string>();
        Compare(original.RootElement, live, "$", changes);
        Console.WriteLine($"READ-ONLY activity metadata diff: {changes.Count} changed paths.");
        foreach (var path in changes.Take(40)) Console.WriteLine($"  {path}");
        if (changes.Count > 40) Console.WriteLine($"  ... {changes.Count - 40} additional paths omitted.");
        Console.WriteLine("No Garmin writes.");
        return 0;
    }

    private static void Compare(JsonElement before, JsonElement after, string path, List<string> changed)
    {
        if (JsonElement.DeepEquals(before, after)) return;
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            var a = before.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
            var b = after.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
            foreach (var name in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!a.TryGetValue(name, out var oldValue) || !b.TryGetValue(name, out var newValue))
                    changed.Add($"{path}.{name} (added/removed)");
                else Compare(oldValue, newValue, $"{path}.{name}", changed);
            }
            return;
        }
        if (before.ValueKind == JsonValueKind.Array && after.ValueKind == JsonValueKind.Array)
        {
            if (before.GetArrayLength() != after.GetArrayLength()) { changed.Add($"{path} (array length changed)"); return; }
            for (var i = 0; i < before.GetArrayLength(); i++) Compare(before[i], after[i], $"{path}[{i}]", changed);
            return;
        }
        changed.Add($"{path} (value changed)");
    }
}
