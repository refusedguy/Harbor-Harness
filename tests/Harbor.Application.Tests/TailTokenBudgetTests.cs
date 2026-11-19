// #2680 F054. Observable contract of the preserved-tail budget policy: the
// 25% share, the 2k..8k clamp, and the no-overflow edge. These are behaviour
// tests: they assert the numbers a caller receives, not how the arithmetic is
// written.

using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

public class TailTokenBudgetTests
{
    [Test]
    public async Task Constants_HaveDocumentedValues()
    {
        int min = TailTokenBudget.MinTokens;
        int max = TailTokenBudget.MaxTokens;
        int percent = TailTokenBudget.UsablePercent;

        await Assert.That(min).IsEqualTo(2_000);
        await Assert.That(max).IsEqualTo(8_000);
        await Assert.That(percent).IsEqualTo(25);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-10_000)]
    [Arguments(int.MinValue)]
    public async Task FromUsableTokens_NonPositiveInput_ReturnsMinimum(int usableTokens)
    {
        var budget = TailTokenBudget.FromUsableTokens(usableTokens);

        await Assert.That(budget).IsEqualTo(TailTokenBudget.MinTokens);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2_000)]
    [Arguments(7_999)]
    [Arguments(8_000)]
    public async Task FromUsableTokens_ShareBelowMinimum_ReturnsMinimum(int usableTokens)
    {
        // 25% of these inputs is at most 2,000, so the lower bound wins.
        var budget = TailTokenBudget.FromUsableTokens(usableTokens);

        await Assert.That(budget).IsEqualTo(TailTokenBudget.MinTokens);
    }

    [Test]
    [Arguments(8_001, 2_000)]
    [Arguments(12_000, 3_000)]
    [Arguments(20_000, 5_000)]
    [Arguments(32_000, 8_000)]
    public async Task FromUsableTokens_ShareInsideRange_ReturnsShare(int usableTokens, int expected)
    {
        var budget = TailTokenBudget.FromUsableTokens(usableTokens);

        await Assert.That(budget).IsEqualTo(expected);
    }

    [Test]
    [Arguments(32_001)]
    [Arguments(40_000)]
    [Arguments(1_000_000)]
    [Arguments(int.MaxValue)]
    public async Task FromUsableTokens_ShareAboveMaximum_ReturnsMaximum(int usableTokens)
    {
        var budget = TailTokenBudget.FromUsableTokens(usableTokens);

        await Assert.That(budget).IsEqualTo(TailTokenBudget.MaxTokens);
    }

    [Test]
    [Arguments(int.MinValue, TailTokenBudget.MinTokens)]
    [Arguments(1_999, TailTokenBudget.MinTokens)]
    [Arguments(2_000, 2_000)]
    [Arguments(5_000, 5_000)]
    [Arguments(8_000, 8_000)]
    [Arguments(8_001, TailTokenBudget.MaxTokens)]
    [Arguments(int.MaxValue, TailTokenBudget.MaxTokens)]
    public async Task Clamp_AnyRequest_StaysWithinBounds(int requested, int expected)
    {
        var budget = TailTokenBudget.Clamp(requested);

        await Assert.That(budget).IsEqualTo(expected);
    }

    [Test]
    public async Task Clamp_IsIdempotent()
    {
        const int requested = 123_456;

        var once = TailTokenBudget.Clamp(requested);
        var twice = TailTokenBudget.Clamp(once);

        await Assert.That(twice).IsEqualTo(once);
    }

    [Test]
    public async Task FromUsableTokens_ResultIsAlwaysClamped()
    {
        for (int usableTokens = -100; usableTokens <= 40_000; usableTokens += 37)
        {
            var budget = TailTokenBudget.FromUsableTokens(usableTokens);

            await Assert.That(budget).IsGreaterThanOrEqualTo(TailTokenBudget.MinTokens);
            await Assert.That(budget).IsLessThanOrEqualTo(TailTokenBudget.MaxTokens);
        }
    }
}
