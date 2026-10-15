using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Web;

/// <summary>
/// Garmin replace-all adapter. A single specifically authorized local trial can enable live PUT.
/// </summary>
internal static class GarminWriteTransportAdapter
{
    // Explicit one-activity opt-in on the user's local Windows machine only.
    internal const long TrialActivityId = 24006250902;
    internal static bool LiveWriteEnabled =>
        OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("GARMIN_LIVE_TRIAL") == TrialActivityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal delegate Task Replace(long activityId, JsonElement fullPayload);

    internal static async Task SendAsync(long activityId, string fullPayloadJson,
        Replace? simulatedReplace = null)
    {
        if (activityId<=0) throw new ArgumentOutOfRangeException(nameof(activityId));
        GarminExerciseSetsWriteContract.Validate(fullPayloadJson,activityId);
        using var payload=JsonDocument.Parse(fullPayloadJson);
        if(simulatedReplace is not null)
        {
            await simulatedReplace(activityId,payload.RootElement);
            return;
        }
        throw new InvalidOperationException("Live Garmin PUT is disabled in source code.");
    }

    // The existing transport is referenced here but this method cannot be
    // invoked through SendAsync while LiveWriteEnabled remains false.
    // Kept separate to make the future production security boundary explicit.
    internal static Task SendActualAsync(GarminGuardedWriteContextFactory.Scope guardedScope,
        long activityId,JsonElement completePayload)
    {
        if(!LiveWriteEnabled || activityId != TrialActivityId)
            throw new InvalidOperationException("Live Garmin PUT is disabled for this activity.");
        ArgumentNullException.ThrowIfNull(guardedScope);
        return GarminExerciseSetsReplaceTransport.ReplaceAsync(
            guardedScope.Context,activityId,completePayload);
    }
}
