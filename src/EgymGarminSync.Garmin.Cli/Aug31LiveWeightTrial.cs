using System.Security.Cryptography;
using System.Text.Json;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Explicitly gated, single-use live trial and separately invoked restore.</summary>
internal static class Aug31LiveWeightTrial
{
    private const long Id = 24180037460L;
    private const string OriginalSetsSha = "86E410CA599B4178AF66867438E61A0B9D62026F508D9E6AA30E405D70A332B8";
    private const string OriginalActivitySha = "4DF6E4DC588F297D199C4870650730B5BB51DCAB89B44FF2EA12CB6BDE91268A";

    public static async Task<int> RunAsync(GarminConnectContext context, bool restore)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "backups");
        if (!Directory.Exists(root)) return Block("Snapshot directory missing.");
        var candidates = Directory.GetDirectories(root, $"{Id}-*");
        string? folder = null;
        foreach (var candidate in candidates)
        {
            var mpath = Path.Combine(candidate, "manifest.json");
            if (!File.Exists(mpath)) continue;
            using var m = JsonDocument.Parse(await File.ReadAllTextAsync(mpath));
            if (m.RootElement.GetProperty("GarminActivityId").GetInt64() != Id) continue;
            var entries = m.RootElement.GetProperty("Files").EnumerateArray().ToArray();
            if (entries.Length != 2) continue;
            var expected = new Dictionary<string, string>
            {
                ["activity.json"] = OriginalActivitySha, ["exerciseSets.json"] = OriginalSetsSha
            };
            var valid = true;
            foreach (var entry in entries)
            {
                var name = entry.GetProperty("Filename").GetString();
                if (name is null || !expected.TryGetValue(name, out var sha)) { valid = false; break; }
                var file = Path.Combine(candidate, name);
                if (!File.Exists(file)) { valid = false; break; }
                var bytes = await File.ReadAllBytesAsync(file);
                if (bytes.Length != entry.GetProperty("Length").GetInt32() ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha, StringComparison.Ordinal) ||
                    !string.Equals(entry.GetProperty("Sha256").GetString(), sha, StringComparison.OrdinalIgnoreCase))
                { valid = false; break; }
            }
            if (valid) { folder = candidate; break; }
        }
        if (folder is null) return Block("Original verified August 31 snapshot not found.");

        var originalSets = await File.ReadAllTextAsync(Path.Combine(folder, "exerciseSets.json"));
        var originalActivity = await File.ReadAllTextAsync(Path.Combine(folder, "activity.json"));
        var latestSets = (await context.GetAndDeserialize<JsonElement>(
            $"/activity-service/activity/{Id}/exerciseSets")).GetRawText();
        var currentState = GarminOneSetReadback.Classify(originalSets, latestSets);
        var expectedState = restore ? GarminTrialState.ChangedOneWeight : GarminTrialState.Original;
        if (currentState != expectedState)
            return Block($"Current state {currentState}; expected {expectedState}. No write.");

        var liveActivity = await context.GetAndDeserialize<JsonElement>($"/activity-service/activity/{Id}");
        using (var savedActivity = JsonDocument.Parse(originalActivity))
            if (!GarminActivityMetadataTolerance.IsAcceptable(originalActivity, liveActivity.GetRawText()))
                return Block("Garmin activity metadata changed since original snapshot.");

        var candidateJson = restore
            ? GarminOneSetRestorePlanner.Prepare(originalSets, latestSets, Id)
            : GarminTrialPayloadNormalizer.Normalize(
                OneSetTrialPlanner.Prepare(originalSets, latestSets).ProposedJson, Id);
        GarminExerciseSetsWriteContract.Validate(candidateJson, Id);
        Console.WriteLine(restore
            ? "LIVE RESTORE proposal: Chest Press first set 21 -> 22 kg, activity 24180037460."
            : "LIVE TEST proposal: Chest Press first set 22 -> 21 kg, activity 24180037460.");
        Console.WriteLine("Garmin will receive all 36 exercise sets (replace-all). No other changes intended.");
        Console.WriteLine("Recovery is NOT guaranteed if Garmin modifies other data.");
        Console.Write("Type exactly CONFIRM 24180037460 to send one Garmin PUT: ");
        var confirmation = Console.ReadLine();
        if (confirmation != "CONFIRM 24180037460") return Block("Confirmation not given.");
        using var payload = JsonDocument.Parse(candidateJson);
        try
        {
            await GarminExerciseSetsReplaceTransport.ReplaceAsync(context, Id, payload.RootElement);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PUT result uncertain or failed ({ex.GetType().Name}). Do not retry blindly.");
            return 1;
        }
        var readback = (await context.GetAndDeserialize<JsonElement>(
            $"/activity-service/activity/{Id}/exerciseSets")).GetRawText();
        var state = GarminOneSetReadback.Classify(originalSets, readback);
        var expectedAfter = restore ? GarminTrialState.Original : GarminTrialState.ChangedOneWeight;
        Console.WriteLine($"Readback state: {state}. Expected: {expectedAfter}.");
        if (state != expectedAfter) return Block("Unexpected post-write state. Stop; do not run additional writes blindly.");
        var afterActivity = await context.GetAndDeserialize<JsonElement>($"/activity-service/activity/{Id}");
        using (var saved = JsonDocument.Parse(originalActivity))
            if (!GarminActivityMetadataTolerance.IsAcceptable(originalActivity, afterActivity.GetRawText()))
                return Block("Activity metadata changed after PUT; inspect before proceeding.");
        Console.WriteLine(restore ? "RESTORE VERIFIED in Garmin readback." :
            "ONE-SET TEST VERIFIED. The original 22 kg value is NOT restored yet; run the separate restore command.");
        return 0;
    }

    private static int Block(string reason)
    {
        Console.Error.WriteLine($"Trial blocked: {reason}");
        return 1;
    }
}
