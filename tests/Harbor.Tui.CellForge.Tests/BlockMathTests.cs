using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #481: <see cref="BlockMath.EstimateLines"/> divided by the raw layout width,
/// so a zero/negative width threw <c>DivideByZeroException</c> on the render
/// thread — the invariant only held because every call site wrapped the call
/// by hand. The helper now floors the width at 1 internally, matching the
/// literal copy in <c>TimelineBlocks.CheapEstimate</c>.
/// </summary>
public class BlockMathTests
{
    [Test]
    public async Task EstimateLines_ZeroWidth_DegeneratesToOneColumnPerChar()
    {
        await Assert.That(BlockMath.EstimateLines("hello", 0)).IsEqualTo(5);
    }

    [Test]
    public async Task EstimateLines_NegativeWidth_BehavesAsWidthOne()
    {
        const string text = "alpha\nbeta\ngamma";
        await Assert.That(BlockMath.EstimateLines(text, -12))
            .IsEqualTo(BlockMath.EstimateLines(text, 1));
    }

    [Test]
    public async Task EstimateLines_ZeroWidth_EmptyAndNewlineOnly_StayNonZero()
    {
        await Assert.That(BlockMath.EstimateLines(string.Empty, 0)).IsEqualTo(1);

        // Three empty logical lines — the floor is per logical line, not global.
        await Assert.That(BlockMath.EstimateLines("\n\n", 0)).IsEqualTo(3);
    }

    [Test]
    public async Task EstimateLines_ZeroWidth_StillCountsLogicalLines()
    {
        await Assert.That(BlockMath.EstimateLines("ab\ncd", 0))
            .IsEqualTo(BlockMath.EstimateLines("ab\ncd", 1));
    }

    [Test]
    public async Task EstimateLines_MirrorsStreamingCheapEstimate_AtDegenerateWidth()
    {
        // The two implementations are literal twins; with the width floored to 1
        // they must agree even for the pathological zero-width case. Text is
        // short enough to stay inside the expanded 10-line thinking budget, so
        // no clamp interferes (O10 #1179: collapsed is the constant 1 — the
        // formula twinship lives on the expanded body).
        const string text = "ab\ncd";
        var block = new StreamingThinkingBlock();
        block.SetExpanded(true);
        block.Append(text);

        await Assert.That(block.CheapEstimate(1)).IsEqualTo(4);
        await Assert.That(block.CheapEstimate(0)).IsEqualTo(4);
        await Assert.That(BlockMath.EstimateLines(text, 0)).IsEqualTo(4);
    }
}
