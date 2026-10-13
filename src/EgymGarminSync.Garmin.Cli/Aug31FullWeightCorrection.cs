using System.Security.Cryptography;
using System.Text.Json;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>One specifically approved historical session only; interactive one-shot PUT.</summary>
internal static class Aug31FullWeightCorrection
{
    private const long ActivityId = 24180037460L;
    private const string SetsHash = "86E410CA599B4178AF66867438E61A0B9D62026F508D9E6AA30E405D70A332B8";
    private const string ActivityHash = "B1C79FD118B852B53AA2AD81BAECBCC84E79224457F1543D9F81ED97FE13B0E1";

    private static readonly EgymExerciseWeight[] Targets =
    [
        new(329, 22), new(330, 30), new(331, 44), new(332, 23),
        new(333, 21), new(334, 32), new(335, 33), new(581, 95)
    ];

    public static async Task<int> RunAsync(GarminConnectContext context, bool restore)
    {
        try
        {
            var folder = FindOriginalSnapshot();
            if (folder is null) return Stop("Original post-trial backup missing or integrity check failed.");
            var originalSets = await File.ReadAllTextAsync(Path.Combine(folder, "exerciseSets.json"));
            var originalActivity = await File.ReadAllTextAsync(Path.Combine(folder, "activity.json"));
            var preview = GarminWeightOnlyPreflight.Build(originalSets, Targets);
            if (preview.Changes.Count != 14) return Stop("Expected 14 set changes.");
            var expectedCurrent = restore ? preview.ProposedJson : originalSets;
            var requested = restore ? originalSets : preview.ProposedJson;
            var latestSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}/exerciseSets")).GetRawText();
            if (!GarminFullWeightReadback.Matches(expectedCurrent, latestSets))
                return Stop("Garmin set state does not equal the expected baseline; no PUT.");

            var latestActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}")).GetRawText();
            if (!GarminActivityMetadataTolerance.IsAcceptable(originalActivity, latestActivity))
                return Stop("Activity metadata differs from verified baseline; no PUT.");

            var outgoing = GarminTrialPayloadNormalizer.Normalize(requested, ActivityId);
            GarminExerciseSetsWriteContract.Validate(outgoing, ActivityId);
            if (!restore) GarminWeightOnlyPreflight.ValidateOnlyApprovedWeightsChanged(
                originalSets, requested, preview.Changes);

            Console.WriteLine(restore
                ? "RESTORE proposal: 14 set weights back to pre-correction Garmin values."
                : "FULL CORRECTION proposal: 14 set weights (7 EGYM exercises), activity 24180037460.");
            foreach (var change in preview.Changes.OrderBy(x => x.EgymActivityId).ThenBy(x => x.SetIndex))
            {
                var before = restore ? change.NewWeightGrams : change.OldWeightGrams;
                var after = restore ? change.OldWeightGrams : change.NewWeightGrams;
                Console.WriteLine($"  set={change.SetIndex} machine={change.EgymActivityId}: {before / 1000m} -> {after / 1000m} kg");
            }
            Console.WriteLine("Garmin PUT replaces all 36 sets. No other field changes are intended.");
            Console.WriteLine("This unofficial API may have unexpected side effects; restore is not guaranteed.");
            Console.Write(restore
                ? "Type exactly RESTORE 24180037460 to issue PUT: "
                : "Type exactly APPLY 24180037460 to issue PUT: ");
            if (Console.ReadLine() != (restore ? "RESTORE 24180037460" : "APPLY 24180037460"))
                return Stop("No confirmation; nothing written.");

            // Close the race between preview and write. Recheck sets AND summary after confirmation.
            var justBeforeSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}/exerciseSets")).GetRawText();
            var justBeforeActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}")).GetRawText();
            if (!GarminFullWeightReadback.Matches(expectedCurrent, justBeforeSets) ||
                !GarminActivityMetadataTolerance.IsAcceptable(originalActivity, justBeforeActivity))
                return Stop("State changed during confirmation; no PUT.");

            using var payload = JsonDocument.Parse(outgoing);
            try
            {
                await GarminExerciseSetsReplaceTransport.ReplaceAsync(context, ActivityId, payload.RootElement);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"PUT failed/uncertain ({ex.GetType().Name}). Re-read Garmin before doing anything else; no automatic retry.");
                return 1;
            }

            var afterSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}/exerciseSets")).GetRawText();
            if (!GarminFullWeightReadback.Matches(requested, afterSets))
                return Stop("Unexpected set state after PUT. Do not issue another write blindly.");
            var afterActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}")).GetRawText();
            if (!GarminActivityMetadataTolerance.IsAcceptable(originalActivity, afterActivity))
                return Stop("Unexpected activity metadata after PUT.");
            Console.WriteLine(restore
                ? "RESTORE VERIFIED: all 36 sets match the pre-correction snapshot."
                : "FULL CORRECTION VERIFIED: only the 14 approved weights differ across 36 sets.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Correction stopped ({ex.GetType().Name}); no further actions attempted.");
            return 1;
        }
    }

    private static string? FindOriginalSnapshot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "backups");
        if (!Directory.Exists(root)) return null;
        foreach (var folder in Directory.GetDirectories(root, $"{ActivityId}-*"))
        {
            var manifestFile = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestFile)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
            if (doc.RootElement.GetProperty("GarminActivityId").GetInt64() != ActivityId) continue;
            var entries = doc.RootElement.GetProperty("Files").EnumerateArray().ToArray();
            if (entries.Length != 2) continue;
            var hashes = new Dictionary<string, string>
            {
                ["activity.json"] = ActivityHash, ["exerciseSets.json"] = SetsHash
            };
            var good = true;
            foreach (var entry in entries)
            {
                var name = entry.GetProperty("Filename").GetString();
                if (name is null || !hashes.TryGetValue(name, out var expected)) { good = false; break; }
                var file = Path.Combine(folder, name);
                if (!File.Exists(file)) { good = false; break; }
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length != entry.GetProperty("Length").GetInt32() ||
                    Convert.ToHexString(SHA256.HashData(bytes)) != expected ||
                    !string.Equals(entry.GetProperty("Sha256").GetString(), expected, StringComparison.OrdinalIgnoreCase))
                { good = false; break; }
            }
            if (good && entries.Select(e => e.GetProperty("Filename").GetString()).Distinct().Count() == 2)
                return folder;
        }
        return null;
    }

    private static int Stop(string reason)
    {
        Console.Error.WriteLine($"Full correction blocked: {reason}");
        return 1;
    }
}
