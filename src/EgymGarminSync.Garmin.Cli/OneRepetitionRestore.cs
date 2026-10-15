using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Single-activity restoration. Offline validation by default; writing requires a separate command and confirmation.</summary>
internal static class OneRepetitionRestore
{
    private const long Id = 24032098016L;
    private const int Index = 2;
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    internal static async Task<int> RunAsync(GarminConnectContext? context, bool execute, string backupFolder)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EgymGarminSync", "live-trials"));
            var folder = Path.GetFullPath(backupFolder);
            var parent = Path.GetDirectoryName(folder);
            if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(folder).StartsWith($"{Id}-", StringComparison.Ordinal) ||
                !Directory.Exists(folder))
                return Stop("Backup folder must be a direct child of the local live-trials directory for the target activity.");
            var manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) return Stop("Backup manifest missing.");
            var m = JsonSerializer.Deserialize<TrialManifest>(await File.ReadAllTextAsync(manifestPath));
            if (m is null || m.ActivityId != Id || m.SetIndex != Index || m.From != 15 || m.To != 14)
                return Stop("Backup manifest does not match the approved repetition trial.");
            var activityJson = await File.ReadAllTextAsync(Path.Combine(folder, "activity.json"));
            var before = await File.ReadAllTextAsync(Path.Combine(folder, "exerciseSets.json"));
            var changed = await File.ReadAllTextAsync(Path.Combine(folder, "proposed.json"));
            if (Hash(activityJson) != m.ActivitySha256 || Hash(before) != m.SetsSha256 ||
                Hash(changed) != m.ProposedSha256)
                return Stop("Snapshot file checksum mismatch.");
            using (var backup = JsonDocument.Parse(activityJson))
                if (!backup.RootElement.TryGetProperty("activityId",out var id) ||
                    !id.TryGetInt64(out var number) || number != Id)
                    return Stop("Activity metadata backup identity mismatch.");
            var forward = GarminSetFieldDraft.Apply(before, [new SetFieldEdit(Index,"repetitionCount",15,14)]);
            if (!GarminFullWeightReadback.Matches(forward,changed))
                return Stop("Saved candidate is not the exact approved one-repetition change.");
            var restoration = GarminTrialPayloadNormalizer.Normalize(before, Id);
            GarminExerciseSetsWriteContract.Validate(restoration,Id);
            Console.WriteLine($"RESTORE PLAN VERIFIED: activity {Id}, set {Index}, 14 -> 15 repetitions.");
            Console.WriteLine($"Recovery files: {folder}; sets SHA256={m.SetsSha256}");
            Console.WriteLine("No automated rollback or retries. Garmin restoration has not been proven.");
            if (!execute) { Console.WriteLine("OFFLINE ONLY: no Garmin GET/PUT."); return 0; }
            if (context is null) return Stop("Garmin context required.");
            var liveSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{Id}/exerciseSets")).GetRawText();
            var liveActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{Id}")).GetRawText();
            if (!GarminFullWeightReadback.Matches(changed,liveSets) ||
                !GarminActivityMetadataTolerance.IsAcceptable(activityJson,liveActivity))
                return Stop("Garmin is not in exact expected post-trial state. No PUT.");
            Console.Write($"Type exactly RESTORE REPS {Id} to issue one PUT: ");
            if (Console.ReadLine() != $"RESTORE REPS {Id}") return Stop("Restore not confirmed.");
            liveSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{Id}/exerciseSets")).GetRawText();
            liveActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{Id}")).GetRawText();
            if (!GarminFullWeightReadback.Matches(changed,liveSets) ||
                !GarminActivityMetadataTolerance.IsAcceptable(activityJson,liveActivity) ||
                Hash(await File.ReadAllTextAsync(Path.Combine(folder,"exerciseSets.json"))) != m.SetsSha256)
                return Stop("Changed state or recovery backup. No PUT.");
            using var payload = JsonDocument.Parse(restoration);
            try { await GarminExerciseSetsReplaceTransport.ReplaceAsync(context,Id,payload.RootElement); }
            catch (Exception) { return Stop("RESTORE PUT failed or uncertain. Do not retry blindly."); }
            var resultSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{Id}/exerciseSets")).GetRawText();
            var resultActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{Id}")).GetRawText();
            if (!GarminFullWeightReadback.Matches(before,resultSets) ||
                !GarminActivityMetadataTolerance.IsAcceptable(activityJson,resultActivity))
                return Stop("Restore readback unexpected. Stop; inspect Garmin manually.");
            Console.WriteLine("RESTORE VERIFIED: set 2 returned to 15 reps, all 36 sets and activity metadata checked.");
            return 0;
        }
        catch(Exception ex) { return Stop($"Restoration stopped ({ex.GetType().Name}). No automatic retry."); }
    }

    private static int Stop(string message) { Console.Error.WriteLine(message); return 1; }
    private sealed record TrialManifest(long ActivityId,int SetIndex,int From,int To,
        string ActivitySha256,string SetsSha256,string ProposedSha256,DateTimeOffset CapturedUtc);
}
