using System.Globalization;
using System.Text;

using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// #46 S6 / #421 — differential equivalence at the streaming chunk boundary,
/// and what a surrogate pair does when a cut lands inside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pair under test.</b> The same text reaches the renderer by two routes:
/// one-shot (a single <see cref="StreamingMarkdownRenderer.Push"/>) and chunked
/// (the identical characters, split at a chosen index, pushed in order). The
/// one-shot route <b>is</b> the oracle — it is the same stream, fed whole. No
/// hand-written expectation is involved, so the test cannot inherit an error
/// from a second implementation that happens to be wrong the same way.
/// </para>
/// <para>
/// <b>Why per-frame, not just final.</b> A chunked feed and a one-shot feed
/// converge on the same final text, so comparing only the last frame passes
/// even while the stream visibly flashed a replacement glyph. The user-visible
/// defect is the <i>intermediate</i> frame, so the per-frame invariant is the
/// one that carries weight: while the accumulated text contains no unpaired
/// surrogate, no painted cell may hold U+FFFD.
/// </para>
/// </remarks>
public class StreamingChunkBoundaryTests
{
    private const string Emo = "\U0001F600";  // U+1F600, UTF-16 D83D DE00
    private const string Wide = "漢字";  // 2 cells, 1 UTF-16 unit each
    private const string Flag = "\U0001F1FA\U0001F1F8";  // regional indicator pair
    private const string ZwjFamily = "\U0001F468\u200D\U0001F469";  // man + ZWJ + woman

    /// <summary>Text wide enough that the wrap seam is exercised too, with pairs at every edge.</summary>
    private const string Body = Emo + " hello " + Wide + " " + Emo + Emo + " tail " + Flag + " done\n";

    private const int Width = 24;

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Paints one rendered frame into a real <see cref="ScreenBuffer"/> and
    /// reads the cell codepoints back. Mirrors
    /// <c>AssistantMarkdownBlock.PaintLine</c> (3 lines of sink) so the frame is
    /// the product's own cell grid — the pair is the <i>feed</i> that differs
    /// between the two routes, never the sink.
    /// </summary>
    private static int[] Frame(StreamingMarkdownRenderer renderer, int width, int cols = 96)
    {
        renderer.RenderTail(width);
        int rows = Math.Max(1, renderer.LineCount);
        var buffer = new ScreenBuffer(cols, rows);
        for (int i = 0; i < renderer.LineCount && i < rows; i++)
        {
            MdLine line = renderer.LineAt(i);
            int cursor = 0;
            for (int s = 0; s < line.Spans.Count; s++)
            {
                MdSpan span = line.Spans[s];
                buffer.SetText(cursor, i, span.Text, CellStyle.Plain);
                cursor += UnicodeWidth.Width(span.Text);
            }
        }

        var frame = new int[cols * rows];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                frame[(y * cols) + x] = buffer.Get(x, y).Rune;
            }
        }

        return frame;
    }

    private static int[] FrameAfterChunks(string text, int[] cutPoints, int width, bool complete)
    {
        var renderer = new StreamingMarkdownRenderer();
        int[] frame = Frame(renderer, width);

        // Feed the identical characters in the identical order; only where the
        // cuts fall differs from the one-shot route.
        var bounds = new List<int> { 0 };
        bounds.AddRange(cutPoints.Where(c => c > 0 && c < text.Length));
        bounds.Add(text.Length);

        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            int from = bounds[i];
            int to = bounds[i + 1];
            if (to > from)
            {
                renderer.Push(text.AsSpan(from, to - from));
                frame = Frame(renderer, width);
            }
        }

        if (complete)
        {
            renderer.Complete();
            frame = Frame(renderer, width);
        }

        return frame;
    }

    /// <summary>Feeds the whole text in one push — the oracle route.</summary>
    private static int[] OneShotFrame(string text, int width)
    {
        var renderer = new StreamingMarkdownRenderer();
        renderer.Push(text);
        renderer.Complete();
        return Frame(renderer, width);
    }

    private static int ReplacementCells(int[] frame) =>
        frame.Count(r => r == '\uFFFD');

    /// <summary>Cells left holding a raw surrogate half (D800..DFFF) — never a legal glyph.</summary>
    private static int LoneSurrogateCells(int[] frame) =>
        frame.Count(r => r is >= 0xD800 and <= 0xDFFF);

    /// <summary>
    /// Exact, ORDER-sensitive key for a frame. <c>IsEqualTo</c> on an array is
    /// reference equality in TUnit, so a per-cell encoding is what makes the
    /// differential comparison of two independently-painted frames meaningful.
    /// </summary>
    private static string FrameKey(int[] frame) => string.Join(",", frame);

    private static string Describe(int[] frame)
    {
        var sb = new StringBuilder();
        foreach (int r in frame)
        {
            sb.Append(r is 0 or ' ' ? ' ' : char.ConvertFromUtf32(r));
        }

        return sb.ToString();
    }

    // ── 1. the differential pair: chunking must not change the frame ───────

    [Test]
    public async Task EverySplitIndex_MatchesTheOneShotFrame()
    {
        string[] corpora = [Emo, Body, Emo + "\n", "a" + Emo + "b\n", Wide + Emo + "\n"];
        int framesChecked = 0;

        foreach (string text in corpora)
        {
            int[] oracle = OneShotFrame(text, Width);
            await Assert.That(ReplacementCells(oracle))
                .IsEqualTo(0)
                .Because($"the one-shot route must itself be clean for {Describe(oracle).Trim()}");

            // Split at EVERY index, including every index inside a pair.
            for (int cut = 1; cut < text.Length; cut++)
            {
                int[] chunked = FrameAfterChunks(text, [cut], Width, complete: true);
                await Assert.That(FrameKey(chunked))
                    .IsEqualTo(FrameKey(oracle))
                    .Because($"cut at {cut} of {text.Length} changed the frame: "
                        + $"one-shot '{Describe(oracle).Trim()}' vs chunked '{Describe(chunked).Trim()}'");
                framesChecked++;
            }
        }

        // Non-vacuity: this test must not be able to pass on an empty corpus.
        await Assert.That(framesChecked).IsGreaterThan(0);
        await Assert.That(corpora.Sum(c => c.Length - 1)).IsGreaterThan(20);
    }

    // ── 2. the property that carries weight: no mid-stream U+FFFD ──────────

    [Test]
    public async Task NoIntermediateFrame_PaintsAReplacementGlyph()
    {
        int framesChecked = 0;
        int pushes = 0;

        // One unit at a time is the worst case: every pair is cut in half.
        foreach (string text in new[] { Body, Emo, Wide + Emo + "\n" })
        {
            var renderer = new StreamingMarkdownRenderer();
            for (int i = 0; i < text.Length; i++)
            {
                renderer.Push(text.AsSpan(i, 1));
                pushes++;

                int[] frame = Frame(renderer, Width);
                framesChecked++;
                int bad = ReplacementCells(frame);

                await Assert.That(bad)
                    .IsEqualTo(0)
                    .Because($"after {i + 1}/{text.Length} units the frame painted {bad} replacement "
                        + $"glyph(s): '{Describe(frame).TrimEnd()}'");
            }

            renderer.Complete();
            await Assert.That(ReplacementCells(Frame(renderer, Width)))
                .IsEqualTo(0)
                .Because($"the completed frame for '{Describe(Frame(renderer, Width)).Trim()}' is not clean");
        }

        await Assert.That(framesChecked).IsGreaterThan(0);
        await Assert.That(pushes).IsGreaterThan(20);
    }

    // ── 3. the detector's positive control (non-vacuity, no reimplementation)

    [Test]
    public async Task Guard_FiresWhenTheInputIsGenuinelyUnpaired()
    {
        // Same detector as test 2 (ReplacementCells), on input where a
        // replacement glyph is the CORRECT output: a lone high surrogate that
        // is followed by more text, so no later chunk can complete it.
        // If this reports 0 the detector in test 2 is vacuous.
        var renderer = new StreamingMarkdownRenderer();
        renderer.Push("a\uD83Db\n");
        renderer.Complete();

        int[] frame = Frame(renderer, Width);
        await Assert.That(ReplacementCells(frame))
            .IsGreaterThan(0)
            .Because("an unpaired high surrogate must render as U+FFFD — the frame is "
                + $"'{Describe(frame).Trim()}'");

        // …and the held-back half is not invented: a well-formed pair at the
        // same position produces none, so the control discriminates.
        var clean = new StreamingMarkdownRenderer();
        clean.Push("a" + Emo + "b\n");
        clean.Complete();
        await Assert.That(ReplacementCells(Frame(clean, Width))).IsEqualTo(0);
    }

    [Test]
    public async Task SplitPair_ReleasesExactlyOneReplacement_OnCompletion()
    {
        // A stream that ends between the halves can never be completed. The
        // held unit is released, and both repository decoders
        // (TextWrap.MeasureFit, ScreenBuffer.SetText) agree that one unpaired
        // unit is one U+FFFD — not two.
        var renderer = new StreamingMarkdownRenderer();
        renderer.Push("x\uD83D");
        await Assert.That(renderer.IsComplete).IsFalse();
        renderer.Complete();

        int[] frame = Frame(renderer, Width);
        await Assert.That(ReplacementCells(frame))
            .IsEqualTo(1)
            .Because($"expected exactly one replacement glyph, got '{Describe(frame).TrimEnd()}'");
    }

    // ── 4. adjacent boundaries: what else a cut can halve ──────────────────

    [Test]
    public async Task AdjacentBoundaries_AreAlsoHeldAtPairLevel()
    {
        // Not just the plain emoji. Each of these is ONE grapheme cluster built
        // from MORE THAN ONE UTF-16 unit, so a unit-at-a-time feed splits it
        // somewhere. Rune count is the wrong denominator here — «e» + U+0301 is
        // two runes and two units, yet still one cluster — so the recorded
        // shape is (units, runes, text elements) and the claim is units >
        // text elements. Measured first, so editing a literal cannot quietly
        // weaken the assertion below.
        (string Name, string Text, int Units, int Runes, int Clusters)[] clusters =
        [
            ("emoji surrogate pair", Emo, Emo.Length, 1, 1),
            ("regional indicator pair (flag)", Flag, Flag.Length, 2, 1),
            ("ZWJ sequence", ZwjFamily, ZwjFamily.Length, 3, 1),
            ("combining mark", "e\u0301", "e\u0301".Length, 2, 1),
        ];

        foreach ((string name, string text, int units, int runes, int expectedClusters) in clusters)
        {
            int textElements = new StringInfo(text).LengthInTextElements;
            await Assert.That(text.Length).IsEqualTo(units).Because($"{name}: recorded unit count");
            await Assert.That(text.EnumerateRunes().Count()).IsEqualTo(runes).Because($"{name}: recorded rune count");
            await Assert.That(textElements).IsEqualTo(expectedClusters).Because($"{name}: recorded cluster count");
            await Assert.That(units).IsGreaterThan(expectedClusters)
                .Because($"{name}: {units} units over {expectedClusters} cluster(s) means a unit-wise cut splits it");

            // The hold is one UTF-16 unit and only ever a HIGH SURROGATE, so
            // the claim is specific: splitting between the units of a pair
            // paints nothing broken. A cut BETWEEN two runes of a longer
            // cluster (between the two regional indicators, between a base and
            // its combining mark, across a ZWJ) is NOT held — nothing claims
            // otherwise, and these are the boundaries a follow-up would own.
            var renderer = new StreamingMarkdownRenderer();
            for (int i = 0; i < text.Length; i++)
            {
                renderer.Push(text.AsSpan(i, 1));
                int[] frame = Frame(renderer, Width);
                await Assert.That(ReplacementCells(frame))
                    .IsEqualTo(0)
                    .Because($"{name}: unit {i} of {units} painted a replacement glyph");
                await Assert.That(LoneSurrogateCells(frame))
                    .IsEqualTo(0)
                    .Because($"{name}: unit {i} of {units} left a raw surrogate half in a cell");
            }

            renderer.Complete();
            await Assert.That(ReplacementCells(Frame(renderer, Width)))
                .IsEqualTo(0)
                .Because($"{name}: completed frame is not clean");
        }
    }

    // ── 5. the sink rule itself is unchanged ───────────────────────────────

    [Test]
    public async Task ScreenBuffer_StillRendersAnUnpairedRunAsOneReplacementPerUnit()
    {
        // Pins the rule the fix routes AROUND rather than changes:
        // ScreenBuffer is a stateless per-call sink, so a run handed to it with
        // a lone half still yields U+FFFD. This is why the hold belongs in the
        // source buffer, not here.
        var buffer = new ScreenBuffer(16, 1);
        buffer.SetText(0, 0, "\uD83D", CellStyle.Plain);

        await Assert.That(buffer.Get(0, 0).Rune)
            .IsEqualTo('\uFFFD')
            .Because("the sink's documented rule is unchanged by #421");
    }
}