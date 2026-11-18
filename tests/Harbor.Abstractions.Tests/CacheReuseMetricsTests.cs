// CacheReuseMetricsTests.cs. Issue #2671, item D04.
//
// Prefix reuse is only meaningful together with the identity of the prefix
// that produced it. These tests pin the value contract: counters cannot go
// backwards, a blank identity is not an identity, and combining snapshots is
// additive for counters but conservative for identity.

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Tests;

public class CacheReuseMetricsTests
{
    [Test]
    public async Task NewMetrics_AreEmpty()
    {
        var metrics = new CacheReuseMetrics();

        await Assert.That(metrics.CachedTokens).IsEqualTo(0);
        await Assert.That(metrics.MissedTokens).IsEqualTo(0);
        await Assert.That(metrics.Checkpoints).IsEqualTo(0);
        await Assert.That(metrics.TotalTokens).IsEqualTo(0);
        await Assert.That(metrics.ReuseRatio).IsEqualTo(0d);
        await Assert.That(metrics.IsEmpty).IsTrue();
        await Assert.That(metrics.HasStablePrefix).IsFalse();
        await Assert.That(metrics.PromptPrefixId).IsNull();
    }

    [Test]
    public async Task Counters_ProduceTotalAndRatio()
    {
        var metrics = new CacheReuseMetrics(
            cachedTokens: 30,
            missedTokens: 10,
            checkpoints: 2,
            promptPrefixId: "prefix-a");

        await Assert.That(metrics.CachedTokens).IsEqualTo(30);
        await Assert.That(metrics.MissedTokens).IsEqualTo(10);
        await Assert.That(metrics.Checkpoints).IsEqualTo(2);
        await Assert.That(metrics.TotalTokens).IsEqualTo(40);
        await Assert.That(metrics.ReuseRatio).IsEqualTo(0.75d);
        await Assert.That(metrics.IsEmpty).IsFalse();
        await Assert.That(metrics.HasStablePrefix).IsTrue();
        await Assert.That(metrics.PromptPrefixId).IsEqualTo("prefix-a");
    }

    [Test]
    public async Task Ratio_IsZero_WhenNoTokensWereRecorded()
    {
        var metrics = new CacheReuseMetrics(checkpoints: 1);

        await Assert.That(metrics.TotalTokens).IsEqualTo(0);
        await Assert.That(metrics.ReuseRatio).IsEqualTo(0d);
    }

    [Test]
    public async Task Combine_AddsCountersAndKeepsMatchingPrefix()
    {
        var first = new CacheReuseMetrics(cachedTokens: 3, checkpoints: 1, promptPrefixId: "prefix-a");
        var second = new CacheReuseMetrics(missedTokens: 7, promptPrefixId: "prefix-a");

        CacheReuseMetrics combined = first.Combine(second);

        await Assert.That(combined.CachedTokens).IsEqualTo(3);
        await Assert.That(combined.MissedTokens).IsEqualTo(7);
        await Assert.That(combined.Checkpoints).IsEqualTo(1);
        await Assert.That(combined.TotalTokens).IsEqualTo(10);
        await Assert.That(combined.PromptPrefixId).IsEqualTo("prefix-a");
        await Assert.That(first.MissedTokens).IsEqualTo(0);
        await Assert.That(second.CachedTokens).IsEqualTo(0);
    }

    [Test]
    public async Task Combine_WithDifferentPrefixes_ClearsIdentity()
    {
        var first = new CacheReuseMetrics(cachedTokens: 1, promptPrefixId: "prefix-a");
        var second = new CacheReuseMetrics(cachedTokens: 2, promptPrefixId: "prefix-b");

        CacheReuseMetrics combined = first.Combine(second);

        await Assert.That(combined.CachedTokens).IsEqualTo(3);
        await Assert.That(combined.HasStablePrefix).IsFalse();
        await Assert.That(combined.PromptPrefixId).IsNull();
    }

    [Test]
    public async Task Combine_WithEmpty_IsIdentity()
    {
        var metrics = new CacheReuseMetrics(cachedTokens: 1, missedTokens: 4, promptPrefixId: "prefix-a");

        CacheReuseMetrics combined = metrics.Combine(new CacheReuseMetrics());

        await Assert.That(combined).IsEqualTo(metrics);
        await Assert.That(new CacheReuseMetrics().Combine(metrics)).IsEqualTo(metrics);
    }

    [Test]
    public async Task Constructor_RejectsNegativeCounters()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => new CacheReuseMetrics(cachedTokens: -1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => new CacheReuseMetrics(checkpoints: -2)));
    }

    [Test]
    public async Task Constructor_TreatsBlankPrefixAsUnknown()
    {
        var metrics = new CacheReuseMetrics(promptPrefixId: "   ");

        await Assert.That(metrics.HasStablePrefix).IsFalse();
        await Assert.That(metrics.PromptPrefixId).IsNull();
    }

    [Test]
    public async Task Combine_RejectsNull()
    {
        var metrics = new CacheReuseMetrics();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Task.Run(() => metrics.Combine(null!)));
    }
}
