// ServiceUsageTests.cs — issue #2671 (D07).
//
// Model tokens already have a counter; web tools do not. These tests pin the
// contract of the per-service usage value: each service keeps its own call and
// token counters, snapshots combine without shared mutable state, and a counter
// can never go backwards.

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Tests;

public class ServiceUsageTests
{
    [Test]
    public async Task NewUsage_IsEmpty()
    {
        var usage = new ServiceUsage();

        await Assert.That(usage.SearchCalls).IsEqualTo(0);
        await Assert.That(usage.FetchCalls).IsEqualTo(0);
        await Assert.That(usage.OcrCalls).IsEqualTo(0);
        await Assert.That(usage.SearchTokens).IsEqualTo(0);
        await Assert.That(usage.FetchTokens).IsEqualTo(0);
        await Assert.That(usage.OcrTokens).IsEqualTo(0);
        await Assert.That(usage.TotalCalls).IsEqualTo(0);
        await Assert.That(usage.TotalTokens).IsEqualTo(0);
        await Assert.That(usage.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Counters_KeepServicesSeparate()
    {
        var usage = new ServiceUsage(
            searchCalls: 3,
            fetchCalls: 2,
            ocrCalls: 1,
            searchTokens: 30,
            fetchTokens: 20,
            ocrTokens: 10);

        await Assert.That(usage.SearchCalls).IsEqualTo(3);
        await Assert.That(usage.FetchCalls).IsEqualTo(2);
        await Assert.That(usage.OcrCalls).IsEqualTo(1);
        await Assert.That(usage.SearchTokens).IsEqualTo(30);
        await Assert.That(usage.FetchTokens).IsEqualTo(20);
        await Assert.That(usage.OcrTokens).IsEqualTo(10);
        await Assert.That(usage.TotalCalls).IsEqualTo(6);
        await Assert.That(usage.TotalTokens).IsEqualTo(60);
        await Assert.That(usage.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Combine_AddsEachServiceIndependently()
    {
        var first = new ServiceUsage(searchCalls: 2, searchTokens: 10);
        var second = new ServiceUsage(fetchCalls: 3, ocrCalls: 1, ocrTokens: 4);

        ServiceUsage combined = first.Combine(second);

        await Assert.That(combined.SearchCalls).IsEqualTo(2);
        await Assert.That(combined.FetchCalls).IsEqualTo(3);
        await Assert.That(combined.OcrCalls).IsEqualTo(1);
        await Assert.That(combined.SearchTokens).IsEqualTo(10);
        await Assert.That(combined.FetchTokens).IsEqualTo(0);
        await Assert.That(combined.OcrTokens).IsEqualTo(4);
        await Assert.That(combined.TotalCalls).IsEqualTo(6);
        await Assert.That(combined.TotalTokens).IsEqualTo(14);

        // Neither operand is mutated by the accumulation.
        await Assert.That(first.FetchCalls).IsEqualTo(0);
        await Assert.That(second.SearchTokens).IsEqualTo(0);
    }

    [Test]
    public async Task Combine_WithEmpty_IsIdentity()
    {
        var usage = new ServiceUsage(searchCalls: 1, fetchTokens: 7);

        ServiceUsage combined = usage.Combine(new ServiceUsage());

        await Assert.That(combined).IsEqualTo(usage);
        await Assert.That(new ServiceUsage().Combine(usage)).IsEqualTo(usage);
    }

    [Test]
    public async Task Constructor_RejectsNegativeCounters()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => new ServiceUsage(searchCalls: -1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => new ServiceUsage(ocrTokens: -5)));
    }

    [Test]
    public async Task Combine_RejectsNull()
    {
        var usage = new ServiceUsage();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Task.Run(() => usage.Combine(null!)));
    }
}
