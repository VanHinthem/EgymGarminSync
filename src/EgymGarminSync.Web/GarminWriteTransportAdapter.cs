using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Web;

/// <summary>
/// Garmin replace-all adapter. Live writes require explicit local opt-in and exact journal approval.
/// </summary>
internal static class GarminWriteTransportAdapter
{
    // Old single-activity trial setting does not authorize general writes.
    internal static bool LiveWriteEnabled =>
        OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("GARMIN_WRITE_ENABLED") == "1";
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

    // Only the approved write workflow calls this transport.
    internal static Task SendActualAsync(GarminGuardedWriteContextFactory.Scope guardedScope,
        long activityId,JsonElement completePayload)
    {
        if(!LiveWriteEnabled)
            throw new InvalidOperationException("Live Garmin PUT is disabled for this activity.");
        ArgumentNullException.ThrowIfNull(guardedScope);
        return GarminExerciseSetsReplaceTransport.ReplaceAsync(
            guardedScope.Context,activityId,completePayload);
    }
}
