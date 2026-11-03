using FluentAssertions;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

public class MetricHistoryAggregatorTests
{
    [Fact]
    public void Aggregate_EmptyValues_ReturnsEmptyList()
    {
        MetricHistoryAggregator.Aggregate(new List<double>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_FewerValuesThanMaxBars_KeepsEveryValue()
    {
        // 5 values, maxBars 20 → one bar per measurement, order unchanged
        var values = new List<double> { 10, 20, 30, 40, 50 };

        var result = MetricHistoryAggregator.Aggregate(values, maxBars: 20, exactBars: 3);

        result.Should().Equal(10, 20, 30, 40, 50);
    }

    [Fact]
    public void Aggregate_MoreValuesThanMaxBars_ReturnsMaxBarsCount()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToList();

        var result = MetricHistoryAggregator.Aggregate(values, maxBars: 20, exactBars: 5);

        result.Should().HaveCount(20);
    }

    [Fact]
    public void Aggregate_NewestValuesAreKeptOneToOne()
    {
        // 30 values, the newest 5 must appear unchanged at the end
        var values = Enumerable.Range(1, 30).Select(i => (double)i).ToList();

        var result = MetricHistoryAggregator.Aggregate(values, maxBars: 10, exactBars: 5);

        result.TakeLast(5).Should().Equal(26, 27, 28, 29, 30);
    }

    [Fact]
    public void Aggregate_OlderValuesAreAggregatedToAverages()
    {
        // 4 old values (1..4) collapsed into a single bucket of average 2.5, then 5 exact values
        var values = Enumerable.Range(1, 9).Select(i => (double)i).ToList();

        var result = MetricHistoryAggregator.Aggregate(values, maxBars: 6, exactBars: 5);

        result.Should().HaveCount(6);
        result[0].Should().BeApproximately(2.5, 0.0001);
        result.TakeLast(5).Should().Equal(5, 6, 7, 8, 9);
    }

    [Fact]
    public void Aggregate_ExactBarsZero_AggregatesEverything()
    {
        // 10 values, no exact bars → all values are aggregated into 5 buckets
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToList();

        var result = MetricHistoryAggregator.Aggregate(values, maxBars: 5, exactBars: 0);

        result.Should().HaveCount(5);
        // Every bar is the average of a bucket, so no bar exceeds the largest value
        result.Should().OnlyContain(v => v >= 1 && v <= 10);
        // The newest bucket holds the newest values, so its average is the highest
        result.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Aggregate_ExactBarsGreaterThanMaxBars_IsCapped()
    {
        var values = Enumerable.Range(1, 30).Select(i => (double)i).ToList();

        var result = MetricHistoryAggregator.Aggregate(values, maxBars: 5, exactBars: 50);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void CalculateLogarithmicBucketSizes_BucketsSumToTotalEntries()
    {
        var sizes = MetricHistoryAggregator.CalculateLogarithmicBucketSizes(97, 15);

        sizes.Should().HaveCount(15);
        sizes.Sum().Should().Be(97);
        sizes.Should().OnlyContain(s => s >= 1);
    }

    [Fact]
    public void CalculateLogarithmicBucketSizes_FewerEntriesThanBuckets_OneEntryPerBucket()
    {
        var sizes = MetricHistoryAggregator.CalculateLogarithmicBucketSizes(3, 15);

        sizes.Should().HaveCount(3);
        sizes.Should().OnlyContain(s => s == 1);
    }

    [Fact]
    public void CalculateLogarithmicBucketSizes_BucketsGrowTowardsOlderData()
    {
        var sizes = MetricHistoryAggregator.CalculateLogarithmicBucketSizes(97, 15);

        // The newest-old bucket is the smallest, the oldest the largest
        sizes.First().Should().BeLessThan(sizes.Last());
    }

    [Fact]
    public void CalculateLogarithmicBucketSizes_InvalidInput_ReturnsEmptyList()
    {
        MetricHistoryAggregator.CalculateLogarithmicBucketSizes(0, 5).Should().BeEmpty();
        MetricHistoryAggregator.CalculateLogarithmicBucketSizes(10, 0).Should().BeEmpty();
    }
}
