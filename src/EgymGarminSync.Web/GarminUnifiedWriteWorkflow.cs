using System.Text.Json;
using global::Garmin.Connect;

namespace EgymGarminSync.Web;

/// <summary>
/// Internal production-shaped write orchestration. No public API/DI registration.
/// Both GET and PUT use the same per-attempt guarded SDK context.
/// Live network transport is hard-disabled; caller must supply a synthetic handler.
/// </summary>
internal static class GarminUnifiedWriteWorkflow
{
    internal static async Task<string> ExecuteIsolatedAsync(
        SyncDbContext db,long activityId,long journalId,
        GarminGuardedWriteContextFactory.Scope guardedScope)
    {
        ArgumentNullException.ThrowIfNull(guardedScope);
        GarminTransactionDryRun.Read read=async id=>
        {
            var activity=await guardedScope.Context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{id}");
            var sets=await guardedScope.Context.GetAndDeserialize<JsonElement>(
                $"/activity-service/activity/{id}/exerciseSets");
            return new GarminTransactionDryRun.Observation(
                activity.GetRawText(),sets.GetRawText());
        };
        GarminTransactionDryRun.Write write=async (id,outgoing)=>
        {
            // An actual Garmin write is NOT enabled here. For the isolated test,
            // only the caller-created synthetic HTTP handler can receive this PUT.
            if(GarminWriteTransportAdapter.LiveWriteEnabled)
                throw new InvalidOperationException("Isolation requires live writes disabled.");
            using var payload=JsonDocument.Parse(outgoing);
            using var response=await guardedScope.Context.MakeHttpPut(
                $"/activity-service/activity/{id}/exerciseSets",payload.RootElement);
            if(!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Synthetic PUT refused.");
        };
        return await GarminTransactionDryRun.ExecuteVerifiedIsolationAsync(
            db,activityId,journalId,read,write);
    }
}
