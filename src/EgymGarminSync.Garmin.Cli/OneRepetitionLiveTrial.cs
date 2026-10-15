using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>
/// Explicit one-off repetition trial. Not reachable from the web application.
/// A separate confirmation is required to issue the sole PUT.
/// </summary>
internal static class OneRepetitionLiveTrial
{
    private const long ActivityId = 24032098016L;
    private const int SetIndex = 2;
    private const int Before = 15;
    private const int After = 14;

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static async Task<int> RunAsync(GarminConnectContext context)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgymGarminSync", "live-trials",
            $"{ActivityId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        try
        {
            // Fail closed when today's activity is no longer the intended EGYM workout.
            var client = new GarminConnectClient(context);
            var activity = (await client.GetActivities(0, 100))
                .SingleOrDefault(a => a.ActivityId == ActivityId);
            if (activity is null || activity.ActivityName is not ("EGYM Positief" or "EGYM Negatief"))
                return Refuse("Target EGYM activity not found in recent Garmin history.");

            var activityJson = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}")).GetRawText();
            var original = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}/exerciseSets")).GetRawText();
            var edits = new[] { new SetFieldEdit(SetIndex, "repetitionCount", Before, After) };
            var proposed = GarminSetFieldDraft.Apply(original, edits);
            var outgoing = GarminTrialPayloadNormalizer.Normalize(proposed, ActivityId);
            GarminExerciseSetsWriteContract.Validate(outgoing, ActivityId);

            // Durable, immutable on-disk recovery source, before permission to write.
            Directory.CreateDirectory(folder);
            await SaveAsync(folder, "activity.json", activityJson);
            await SaveAsync(folder, "exerciseSets.json", original);
            await SaveAsync(folder, "proposed.json", proposed);
            var manifest = new TrialManifest(ActivityId, SetIndex, Before, After,
                Hash(activityJson), Hash(original), Hash(proposed), DateTimeOffset.UtcNow);
            await SaveAsync(folder, "manifest.json", JsonSerializer.Serialize(manifest));
            if (!Verify(folder, manifest))
                return Refuse("Pre-write backup verification failed. No PUT.");

            Console.WriteLine($"READ-ONLY PREPARATION PASSED: activity {ActivityId}, set {SetIndex}, reps {Before} -> {After}.");
            Console.WriteLine($"Backup: {folder}");
            Console.WriteLine("Garmin replaces ALL 36 sets. No automatic retry or restore.");
            Console.WriteLine("Recovery via Garmin is NOT guaranteed. The backup files must be preserved.");
            Console.Write($"Type exactly APPLY REPS {ActivityId} to issue one PUT, otherwise press Enter: ");
            if (Console.ReadLine() != $"APPLY REPS {ActivityId}")
                return Refuse("Confirmation not given. No Garmin write.");

            // Recheck BOTH sources immediately after confirmation. Any mismatch aborts.
            var justBeforeActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}")).GetRawText();
            var justBeforeSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}/exerciseSets")).GetRawText();
            if (!GarminActivityMetadataTolerance.IsAcceptable(activityJson, justBeforeActivity) ||
                !GarminFullWeightReadback.Matches(original, justBeforeSets) ||
                !Verify(folder, manifest))
                return Refuse("Garmin state or backup changed before PUT; nothing written.");

            using var payload = JsonDocument.Parse(outgoing);
            try
            {
                await GarminExerciseSetsReplaceTransport.ReplaceAsync(context, ActivityId, payload.RootElement);
            }
            catch (Exception)
            {
                return Refuse("PUT failure or unknown result. NO RETRY. Inspect Garmin and backup manually.");
            }
            // No second write even when readback is inconsistent.
            var postSets = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}/exerciseSets")).GetRawText();
            var postActivity = (await context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{ActivityId}")).GetRawText();
            if (!GarminFullWeightReadback.Matches(proposed, postSets) ||
                !GarminActivityMetadataTolerance.IsAcceptable(activityJson, postActivity))
                return Refuse("Unexpected live readback. Stop; preserve backup, no automatic correction.");
            Console.WriteLine("ONE REPETITION LIVE TEST VERIFIED: 15 -> 14; full sets and metadata readback checked.");
            Console.WriteLine($"Recovery source preserved: {folder}. No restore attempted.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Trial aborted ({ex.GetType().Name}). Do not retry after uncertain PUT.");
            return 1;
        }
    }

    private static async Task SaveAsync(string folder, string name, string json)
    {
        var path = Path.Combine(folder, name);
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static bool Verify(string folder, TrialManifest manifest)
    {
        try
        {
            return Hash(File.ReadAllText(Path.Combine(folder, "activity.json"))) == manifest.ActivitySha256 &&
                Hash(File.ReadAllText(Path.Combine(folder, "exerciseSets.json"))) == manifest.SetsSha256 &&
                Hash(File.ReadAllText(Path.Combine(folder, "proposed.json"))) == manifest.ProposedSha256;
        }
        catch (IOException) { return false; }
    }

    private static int Refuse(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed record TrialManifest(long ActivityId, int SetIndex, int From, int To,
        string ActivitySha256, string SetsSha256, string ProposedSha256, DateTimeOffset CapturedUtc);
}
