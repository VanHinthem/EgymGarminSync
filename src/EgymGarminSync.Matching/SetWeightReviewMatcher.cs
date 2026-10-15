namespace EgymGarminSync.Matching;
public sealed record SetWeightReview(long ExerciseId, IReadOnlyList<decimal> EgymKg, IReadOnlyList<decimal?> GarminKg, bool Equal);
public static class SetWeightReviewMatcher
{
    public static IReadOnlyList<SetWeightReview> Compare(
        IReadOnlyDictionary<long, IReadOnlyList<decimal>> egym,
        IReadOnlyCollection<GarminRecordedSet> garmin)
    {
        var results = new List<SetWeightReview>();
        foreach (var entry in egym)
        {
            if (!ExerciseIdentityMapping.TryGetGarminKey(entry.Key, out var key)) continue;
            var g = garmin.Where(x => x.IsActiveStrength && x.Category == key.Category &&
                x.ExerciseName == key.ExerciseName).Select(x => x.WeightGrams / 1000m).ToArray();
            var same = entry.Value.Count == 2 && g.Length == 2 && g.All(x => x.HasValue) &&
                entry.Value.Order().SequenceEqual(g.Select(x => x!.Value).Order());
            results.Add(new(entry.Key, entry.Value, g, same));
        }
        return results;
    }
}