using System.Text.Json;

namespace EgymGarminSync.Matching;

/// <summary>Pure offline recovery preparation; never performs a Garmin request.</summary>
public static class GarminOneSetRestorePlanner
{
    public static string Prepare(string originalSnapshot, string observedLiveSets, long activityId)
    {
        if (GarminOneSetReadback.Classify(originalSnapshot, observedLiveSets) != GarminTrialState.ChangedOneWeight)
            throw new InvalidOperationException("Restore blocked: Garmin does not match the single-weight trial state.");

        var original = JsonDocument.Parse(originalSnapshot);
        try
        {
            if (original.RootElement.GetProperty("activityId").GetInt64() != activityId)
                throw new InvalidOperationException("Restore blocked: original activity ID mismatch.");
            // A restore is a replacement of the exact saved data (augmented only with
            // request-required activity IDs), not an inverse edit of server-returned data.
            var payload = GarminTrialPayloadNormalizer.Normalize(originalSnapshot, activityId);
            GarminExerciseSetsWriteContract.Validate(payload, activityId);
            return payload;
        }
        finally
        {
            original.Dispose();
        }
    }
}
