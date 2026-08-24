using Harbor.Abstractions.Models;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     A7 (sprint 5): first coverage for <see cref="Pricing.CalculateCost" /> —
///     the USD cost function every status bar and cost-limit check relies on.
///     Decimal arithmetic is asserted EXACTLY (no double drift tolerated).
/// </summary>
public class PricingTests
{
    [Test]
    public async Task CalculateCost_ZeroUsage_IsZero()
    {
        var pricing = new Pricing(3m, 15m, 0.30m, 3.75m);

        decimal cost = pricing.CalculateCost(new Usage(0, 0));

        await Assert.That(cost).IsEqualTo(0m);
    }

    [Test]
    public async Task CalculateCost_MillionTokensAtKnownRates_SumsExactly()
    {
        // Claude-Opus-like rates: $3/M in, $15/M out.
        var pricing = new Pricing(3m, 15m);

        decimal cost = pricing.CalculateCost(new Usage(1_000_000, 1_000_000));

        await Assert.That(cost).IsEqualTo(18m);
    }

    [Test]
    public async Task CalculateCost_FractionalMillions_Prorates()
    {
        var pricing = new Pricing(3m, 12m);

        // 500k in → $1.50; 250k out → $3.00; total $4.50.
        decimal cost = pricing.CalculateCost(new Usage(500_000, 250_000));

        await Assert.That(cost).IsEqualTo(4.5m);
    }

    [Test]
    public async Task CalculateCost_CacheComponents_UseTheirRates()
    {
        var pricing = new Pricing(3m, 15m, CacheReadPerMillion: 0.30m, CacheWritePerMillion: 3.75m);

        decimal cost = pricing.CalculateCost(new Usage(
            InputTokens: 100_000,
            OutputTokens: 200_000,
            ReasoningTokens: null,
            CacheReadTokens: 1_000_000,
            CacheWriteTokens: 400_000));

        // in $0.30 + out $3.00 + read $0.30 + write $1.50 = $5.10.
        await Assert.That(cost).IsEqualTo(5.10m);
    }

    [Test]
    public async Task CalculateCost_NullCacheRates_TreatedAsFree()
    {
        var pricing = new Pricing(3m, 15m); // cache rates default to null

        decimal cost = pricing.CalculateCost(new Usage(
            InputTokens: 1_000_000,
            OutputTokens: 0,
            ReasoningTokens: null,
            CacheReadTokens: 999_999,
            CacheWriteTokens: 999_999));

        await Assert.That(cost).IsEqualTo(3m);
    }

    [Test]
    public async Task CalculateCost_UnknownPricing_AlwaysZero()
    {
        var usage = new Usage(10_000_000, 10_000_000, null, 10_000_000, 10_000_000);

        decimal cost = Pricing.Unknown.CalculateCost(usage);

        await Assert.That(cost).IsEqualTo(0m);
    }

    [Test]
    public async Task CalculateCost_SingleToken_NoFloatingPointDrift()
    {
        // Double math would produce binary artifacts here; decimal must be
        // exactly 1×3E-9 + 1×1.5E-8 USD.
        var pricing = new Pricing(0.003m, 0.015m);

        decimal cost = pricing.CalculateCost(new Usage(1, 1));

        await Assert.That(cost).IsEqualTo(0.000000018m);
    }
}
