using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG11 #283 streaming holdback: open fences, growing tables and unclosed
/// math stay buffered (no per-token repaint) until structurally complete;
/// the thinking block reuses its stable wrapped prefix via incremental hash.
/// Pin: final output after completion is identical to a one-shot render.
/// </summary>
public class StreamingHoldbackTests
{
    private static List<string> Flatten(IReadOnlyList<MdLine> lines)
    {
        var flat = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in l.Spans)
            {
                sb.Append('[').Append(s.Style).Append(':').Append(s.Text).Append(']');
            }

            flat.Add(sb.ToString());
        }

        return flat;
    }

    private static StreamingMarkdownRenderer OneShot(string source, int width)
    {
        var r = new StreamingMarkdownRenderer();
        r.Push(source);
        r.Complete();
        _ = r.RenderTail(width);
        return r;
    }

    [Test]
    public async Task OpenFence_HeldBack_UntilClosed()
    {
        var r = new StreamingMarkdownRenderer();
        r.Push("```csharp\nline one\nline two\n");
        bool first = r.RenderTail(40);

        // Only the opener paints; the body stays buffered.
        await Assert.That(first).IsTrue();
        await Assert.That(r.LineCount).IsEqualTo(1);
        await Assert.That(r.FrozenLineCount).IsEqualTo(0);

        // More body deltas must not repaint either.
        r.Push("line three\n");
        await Assert.That(r.RenderTail(40)).IsFalse();
        await Assert.That(r.LineCount).IsEqualTo(1);

        // Closing the fence releases the whole block; final equals one-shot.
        r.Push("```\nafter\n");
        _ = r.RenderTail(40);
        r.Complete();
        _ = r.RenderTail(40);
        await Assert.That(Flatten(r.GetLines()))
            .IsEquivalentTo(Flatten(OneShot("```csharp\nline one\nline two\nline three\n```\nafter\n", 40).GetLines()));
    }

    [Test]
    public async Task GrowingTable_HeldBack_UntilTerminated()
    {
        var r = new StreamingMarkdownRenderer();
        r.Push("| a | b |\n|---|---|\n| 1 | 2 |\n");
        _ = r.RenderTail(40);

        // No stable prefix: column geometry is unknown until the run closes.
        await Assert.That(r.LineCount).IsEqualTo(0);
        await Assert.That(r.FrozenLineCount).IsEqualTo(0);

        r.Push("| 3 | 4 |\n\ntail\n");
        _ = r.RenderTail(40);
        r.Complete();
        _ = r.RenderTail(40);
        await Assert.That(Flatten(r.GetLines()))
            .IsEquivalentTo(Flatten(OneShot("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n\ntail\n", 40).GetLines()));
    }

    [Test]
    public async Task UnclosedMath_HeldBack_UntilClosed()
    {
        var r = new StreamingMarkdownRenderer();
        r.Push("$$\nE = mc^2\n");
        _ = r.RenderTail(40);

        await Assert.That(r.LineCount).IsEqualTo(1);
        await Assert.That(r.FrozenLineCount).IsEqualTo(0);

        r.Push("$$\ndone\n");
        _ = r.RenderTail(40);
        r.Complete();
        _ = r.RenderTail(40);
        await Assert.That(Flatten(r.GetLines()))
            .IsEquivalentTo(Flatten(OneShot("$$\nE = mc^2\n$$\ndone\n", 40).GetLines()));
    }

    [Test]
    public async Task FenceAfterOpenParagraph_HoldsBody_KeepsPrefix()
    {
        var r = new StreamingMarkdownRenderer();
        r.Push("intro line\n```py\nbody\n");
        _ = r.RenderTail(40);

        // Paragraph + fence opener paint; the fence body does not.
        await Assert.That(r.LineCount).IsEqualTo(2);
    }

    [Test]
    public async Task TerminatedPipeRun_FreezesAsText()
    {
        // A «|»-led run closed by foreign text can never become a table —
        // it freezes as plain text instead of hiding until completion.
        var r = new StreamingMarkdownRenderer();
        r.Push("| just text |\nnext\n");
        _ = r.RenderTail(40);
        await Assert.That(r.FrozenLineCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Thinking_StablePrefix_MatchesWrapDocument()
    {
        var b = new StreamingThinkingBlock();
        b.Append("first logical line here\nsecond ");
        int m1 = b.Measure(20).MinLines;
        b.Append("logical line here\nthird");
        int m2 = b.Measure(20).MinLines;
        b.Append(" tail");

        var baseline = new List<string>();
        TextWrap.WrapDocument("first logical line here\nsecond logical line here\nthird tail", 20, baseline);
        await Assert.That(b.Measure(20).MinLines).IsEqualTo(baseline.Count);
        await Assert.That(m1).IsLessThan(m2);
        await Assert.That(b.RawText()).IsEqualTo("first logical line here\nsecond logical line here\nthird tail");
    }

    [Test]
    public async Task Thinking_HashChangesPerAppend_CheapEstimateMatchesFormula()
    {
        var b = new StreamingThinkingBlock();
        ulong h0 = b.ContentHash;
        b.Append("hello world, this is a thinking line\n");
        ulong h1 = b.ContentHash;
        b.Append("more\n");
        ulong h2 = b.ContentHash;

        await Assert.That(h0).IsNotEqualTo(h1);
        await Assert.That(h1).IsNotEqualTo(h2);
        await Assert.That(b.CheapEstimate(10)).IsEqualTo(BlockMath.EstimateLines(b.RawText(), 10));
        await Assert.That(b.Measure(10).MinLines).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Thinking_PaintsStableRows_WithoutThrowing()
    {
        var b = new StreamingThinkingBlock();
        b.Append("thinking line one\nthinking line two\npartial");
        var m = b.Measure(30);
        var buffer = new ScreenBuffer(30, m.MinLines);
        b.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 30, m.MinLines), 0));
        await Assert.That(GridDump.Art(buffer).Contains("thinking line one")).IsTrue();
    }
}
