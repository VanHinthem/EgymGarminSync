using System.Text.Json;
using Garmin.Connect;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>
/// Isolated Garmin exercise-set replacement transport. Not currently wired to a CLI action.
/// The Garmin endpoint replaces the entire exerciseSets collection, not one set.
/// </summary>
internal static class GarminExerciseSetsReplaceTransport
{
    internal static string Endpoint(long activityId)
    {
        if (activityId <= 0) throw new ArgumentOutOfRangeException(nameof(activityId));
        return $"/activity-service/activity/{activityId}/exerciseSets";
    }

    /// <summary>
    /// Performs a single authenticated replacement request. Caller MUST separately validate
    /// source backup, fresh GET baseline, entire 36-set payload and user approval.
    /// This method does not retry, restore, or claim post-write verification.
    /// </summary>
    internal static async Task ReplaceAsync(GarminConnectContext context, long activityId,
        JsonElement validatedCompletePayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (validatedCompletePayload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A complete exerciseSets JSON object is required.", nameof(validatedCompletePayload));
        // In our integration this is called only with explicitly validated normalized payload.
        using var response = await context.MakeHttpPut(Endpoint(activityId),
            validatedCompletePayload, cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Garmin refused exercise-set replacement: {(int)response.StatusCode}.");
    }
}
