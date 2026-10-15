using System.Text.Json;
using EgymGarminSync.Garmin.Cli;
using global::Garmin.Connect;
using global::Garmin.Connect.Auth;
using global::Garmin.Connect.Auth.External;

namespace EgymGarminSync.Web;

/// <summary>
/// Production-shaped Garmin write orchestration. GET and PUT use the same
/// per-attempt guarded SDK context. The source-code live-write gate remains the
/// final transport switch.
/// </summary>
internal static class GarminUnifiedWriteWorkflow
{
    internal static async Task<string> ExecuteLiveAsync(
        SyncDbContext db,long activityId,long journalId)
    {
        if(!GarminWriteTransportAdapter.LiveWriteEnabled)
            return "Disabled";
        if(!GarminTokenCacheProvider.IsSupported)
            return "Blocked";

        var email=Environment.GetEnvironmentVariable("GARMIN_EMAIL");
        var password=Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        if(string.IsNullOrWhiteSpace(email)||string.IsNullOrWhiteSpace(password))
            return "Blocked";

        var tokenCache=new GarminTokenCacheProvider(email);
        if(tokenCache.IsInLoginCooldown(out _))
            return "Blocked";

        using var guardedScope=GarminGuardedWriteContextFactory.Create(
            GarminIpv4Transport.Create(),
            new BasicAuthParameters(email,password),
            new NotImplementedMfaCode(),
            tokenCache);

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
            using var payload=JsonDocument.Parse(outgoing);
            await GarminWriteTransportAdapter.SendActualAsync(
                guardedScope,id,payload.RootElement);
        };

        try
        {
            var result=await GarminTransactionDryRun.ExecuteApprovedVerifiedAsync(
                db,activityId,journalId,read,write);
            if(result=="Verified")
                tokenCache.ClearLoginCooldown();
            return result;
        }
        catch
        {
            // If intent was already persisted as WriteStarted the core catches
            // transport/readback failures and returns Uncertain. Exceptions here
            // therefore mean the write was not started by this workflow.
            return "Blocked";
        }
    }

    internal static async Task<string> ExecuteIsolatedAsync(
        SyncDbContext db,long activityId,long journalId,
        GarminGuardedWriteContextFactory.Scope guardedScope)
    {
        ArgumentNullException.ThrowIfNull(guardedScope);
        if(!guardedScope.IsSyntheticTestContext)
            throw new InvalidOperationException("Only isolated synthetic contexts are permitted.");
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
            if(GarminWriteTransportAdapter.LiveWriteEnabled)
                throw new InvalidOperationException("Isolation requires live writes disabled.");
            using var payload=JsonDocument.Parse(outgoing);
            using var response=await guardedScope.Context.MakeHttpPut(
                $"/activity-service/activity/{id}/exerciseSets",payload.RootElement);
            if(!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Synthetic PUT refused.");
        };
        return await GarminTransactionDryRun.ExecuteApprovedIsolationAsync(
            db,activityId,journalId,read,write);
    }
}
