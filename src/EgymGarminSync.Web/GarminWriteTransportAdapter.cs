using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using EgymGarminSync.Matching;
using Garmin.Connect;

namespace EgymGarminSync.Web;

/// <summary>
/// Garmin replace-all adapter. Live transport is deliberately hard-disabled.
/// Not registered in DI or mapped to an HTTP endpoint.
/// </summary>
internal static class GarminWriteTransportAdapter
{
    // A runtime configuration flag cannot enable this. Removing the source-code
    // gate will require a separate review and explicit live-test authorization.
    internal const bool LiveWriteEnabled = false;
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
        if(!LiveWriteEnabled)
            throw new InvalidOperationException("Live Garmin PUT is disabled in source code.");
        ArgumentNullException.ThrowIfNull(guardedScope);
        return GarminExerciseSetsReplaceTransport.ReplaceAsync(
            guardedScope.Context,activityId,completePayload);
    }
}
