namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Aggregates a metric history into a fixed number of bars for the history charts.
/// The newest entries are shown 1:1 (they carry the status decision), older entries are
/// aggregated into logarithmically growing buckets so that a long history stays readable.
/// Used by the static HTML report and by the WebUI metric tiles, so both show the same bars.
/// </summary>
public static class MetricHistoryAggregator
{
    /// <summary>
    /// Default number of bars in a history chart.
    /// </summary>
    public const int DefaultMaxBars = 20;

    /// <summary>
    /// Aggregates metric values into bars.
    /// </summary>
    /// <param name="valuesOldestFirst">Metric values in chronological order (oldest first).</param>
    /// <param name="maxBars">Maximum number of bars to return.</param>
    /// <param name="exactBars">Number of newest entries kept 1:1.</param>
    /// <returns>The bar values in chronological order (oldest first).</returns>
    public static List<double> Aggregate(IReadOnlyList<double> valuesOldestFirst, int maxBars = DefaultMaxBars, int exactBars = 5)
    {
        if (valuesOldestFirst == null || valuesOldestFirst.Count == 0) return new List<double>();

        maxBars = Math.Max(1, maxBars);

        // Everything fits: one bar per measurement.
        if (valuesOldestFirst.Count <= maxBars) return valuesOldestFirst.ToList();

        exactBars = Math.Min(Math.Max(exactBars, 0), maxBars);
        var aggregatedBars = maxBars - exactBars;

        var newest = valuesOldestFirst.Skip(valuesOldestFirst.Count - exactBars).ToList();
        var older = valuesOldestFirst.Take(valuesOldestFirst.Count - exactBars).ToList();

        if (older.Count == 0 || aggregatedBars <= 0) return newest;

        // Bucket sizes grow logarithmically from the newest to the oldest of the remaining entries.
        var bucketSizes = CalculateLogarithmicBucketSizes(older.Count, aggregatedBars);
        var remainingNewestFirst = new List<double>(older);
        remainingNewestFirst.Reverse();

        var aggregatedBuckets = new List<double>();
        var currentIndex = 0;
        foreach (var bucketSize in bucketSizes)
        {
            if (currentIndex >= remainingNewestFirst.Count) break;

            var bucketValues = remainingNewestFirst.Skip(currentIndex).Take(bucketSize).ToList();
            if (bucketValues.Count > 0) aggregatedBuckets.Add(bucketValues.Average());
            currentIndex += bucketSize;
        }

        // The buckets were collected from the newest to the oldest — reverse them for the
        // chronological order and append the exact (1:1) newest entries.
        aggregatedBuckets.Reverse();
        aggregatedBuckets.AddRange(newest);
        return aggregatedBuckets;
    }

    /// <summary>
    /// Calculates logarithmically increasing bucket sizes.
    /// Smaller buckets for newer data, larger buckets for older data.
    /// The sizes sum up exactly to <paramref name="totalEntries"/> and every bucket holds at least one entry.
    /// </summary>
    /// <param name="totalEntries">Total number of entries to distribute.</param>
    /// <param name="bucketCount">Number of buckets to create.</param>
    /// <returns>Bucket sizes (first = smallest/newest, last = largest/oldest).</returns>
    public static List<int> CalculateLogarithmicBucketSizes(int totalEntries, int bucketCount)
    {
        if (bucketCount <= 0 || totalEntries <= 0) return new List<int>();

        // Fewer entries than buckets: one entry per bucket, the rest stays empty.
        if (totalEntries <= bucketCount)
        {
            return Enumerable.Repeat(1, totalEntries).ToList();
        }

        // Weight of a bucket grows exponentially from the newest to the oldest bucket.
        var logBase = Math.Pow(totalEntries, 1.0 / bucketCount);
        var weights = new List<double>();
        for (var i = 0; i < bucketCount; i++) weights.Add(Math.Pow(logBase, i));
        var weightSum = weights.Sum();

        // Every bucket holds at least one entry; the rest is distributed by weight.
        var remaining = totalEntries - bucketCount;
        var shares = weights.Select(w => w / weightSum * remaining).ToList();

        var sizes = shares.Select(s => (int)Math.Floor(s)).ToList();
        var remainder = remaining - sizes.Sum();

        // Largest-remainder method: the buckets with the biggest fractional part get the
        // entries that are left over. This keeps the sum exactly at totalEntries.
        var order = shares
            .Select((share, index) => (index, fraction: share - Math.Floor(share)))
            .OrderByDescending(o => o.fraction)
            .ThenBy(o => o.index)
            .ToList();

        for (var i = 0; i < remainder; i++) sizes[order[i % order.Count].index]++;

        for (var i = 0; i < sizes.Count; i++) sizes[i]++;

        return sizes;
    }
}
