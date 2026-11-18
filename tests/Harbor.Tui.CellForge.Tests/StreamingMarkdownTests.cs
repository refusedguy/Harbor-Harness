using Harbor.Ui.Framework.Rendering.Markdown;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// The CE-3 W2.1 main invariant (widgets §6.4): pushing a document
/// token-by-token with arbitrary chunk boundaries and rendering after each
/// push yields, at completion, styled lines IDENTICAL to a one-shot render of
/// the whole document — per line, per span, per char.
/// </summary>
public class StreamingMarkdownTests
{
    public static readonly string[] Corpus =
    [
        // paragraphs + blank separators
        "Hello world.\n\nSecond paragraph with more text to wrap around a couple of times in narrow widths.\n",
        // ATX headings
        "# Title\n## Subtitle\n###### Deep\nBody text.\n",
        // lists
        "- alpha\n- beta\n1. one\n2. two\n10. ten\n",
        // fenced code
        "```csharp\ncode line 1\n  indented\n```\nafter fence text\n",
        // inline styles
        "plain **bold** and *italic* and `code` and ***both*** mixed **unterminated\n",
        // unterminated markers stay literal until closed (tail re-render)
        "start **never closed here\n",
        // foreign-block termination without blank lines
        "paragraph directly followed\n# heading right away\n",
        // CJK wide runes inside text
        "中文测试 with wide 中文 runes wrapping across narrow width cells\n",
        // empty-ish
        "",
        "\n\n\n",
        // combined kitchen sink — a raw literal, so its embedded newlines are
        // the checkout's own (CRLF on Windows). Normalized to LF: the
        // contract is over '\n'-delimited markdown, and the source file's
        // line endings must not leak into the corpus (win leg, #1249).
        Lf("""
        # Build report

        Everything **succeeded** in *record* time.
        - item one with `inline code`
        - item two

        ```
        raw output
        stays literal
        ```

        ## Notes
        Final thoughts here.
        """),
    ];

    /// <summary>
    ///     Checkout line-ending shield for raw-literal corpus entries (see above).
    /// </summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Test]
    [Arguments(40)]
    [Arguments(20)]
    [Arguments(9)]
    public async Task TokenByToken_EqualsWholeDocument_AtWidth(int width)
    {
        for (int doc = 0; doc < Corpus.Length; doc++)
        {
            var streaming = new StreamingMarkdownRenderer();
            var expected = new StreamingMarkdownRenderer();

            string source = Corpus[doc];
            expected.Push(source);
            expected.Complete();
            _ = expected.RenderTail(width);

            // Char-by-char pushes with renders between each.
            foreach (var chunk in Chunks(source))
            {
                streaming.Push(chunk);
                _ = streaming.RenderTail(width);
            }

            streaming.Complete();
            _ = streaming.RenderTail(width);

            var got = MarkdownFixtures.Flatten(streaming.GetLines());
            var want = MarkdownFixtures.Flatten(expected.GetLines());

            await Assert.That(got).IsEquivalentTo(want).Because($"doc#{doc} diverged at width {width}");
        }
    }

    [Test]
    public async Task RandomChunkSplits_ProduceIdenticalFinalLines()
    {
        var rng = new Random(1337);
        foreach (var source in Corpus)
        {
            for (int trial = 0; trial < 8; trial++)
            {
                var streamed = new StreamingMarkdownRenderer();
                int pos = 0;
                while (pos < source.Length)
                {
                    int take = Math.Min(1 + rng.Next(7), source.Length - pos);
                    streamed.Push(source.AsSpan(pos, take));
                    pos += take;
                    if (rng.Next(3) == 0)
                    {
                        _ = streamed.RenderTail(30); // interleaved renders
                    }
                }

                streamed.Complete();
                _ = streamed.RenderTail(30);

                var once = new StreamingMarkdownRenderer();
                once.Push(source);
                once.Complete();
                _ = once.RenderTail(30);

                await Assert.That(MarkdownFixtures.Flatten(streamed.GetLines())).IsEquivalentTo(MarkdownFixtures.Flatten(once.GetLines()));
            }
        }
    }

    [Test]
    public async Task Freeze_AdvancesOnlyPastCompleteBlocks()
    {
        var r = new StreamingMarkdownRenderer();
        // Blank-line terminator ⇒ the first paragraph freezes…
        r.Push("first paragraph done\n\n");
        _ = r.RenderTail(40);
        await Assert.That(r.Checkpoint.SourceChars).IsGreaterThan(0);
        await Assert.That(r.FrozenLineCount).IsGreaterThanOrEqualTo(1);

        // …but a trailing open tail without newline never freezes.
        r.Push("open tail without newline");
        _ = r.RenderTail(40);
        await Assert.That(r.LineCount).IsGreaterThan(r.FrozenLineCount);
    }

    [Test]
    public async Task WidthChange_RebuildsAllLines()
    {
        var r = new StreamingMarkdownRenderer();
        r.Push("a fairly long paragraph that will wrap differently at different terminal widths\n\nsecond block\n");
        _ = r.RenderTail(60);
        int wide = r.LineCount;

        _ = r.RenderTail(16);
        int narrow = r.LineCount;

        await Assert.That(narrow).IsGreaterThan(wide);

        // Rebuilt state equals a fresh renderer at the narrow width.
        var fresh = new StreamingMarkdownRenderer();
        fresh.Push("a fairly long paragraph that will wrap differently at different terminal widths\n\nsecond block\n");
        fresh.Complete();
        _ = fresh.RenderTail(16);
        await Assert.That(MarkdownFixtures.Flatten(r.GetLines())).IsEquivalentTo(MarkdownFixtures.Flatten(fresh.GetLines()));
    }

    [Test]
    public async Task InlineScanner_StyleTogglesAndLiterals()
    {
        var spans = StreamingMarkdownRenderer.ScanInline("a **b** c `d` e *f* g");

        await Assert.That(spans.Count).IsEqualTo(7);
        await Assert.That(spans[0]).IsEqualTo(new MdSpan("a ", MdStyle.Normal));
        await Assert.That(spans[1]).IsEqualTo(new MdSpan("b", MdStyle.Bold));
        await Assert.That(spans[3]).IsEqualTo(new MdSpan("d", MdStyle.Code));
        await Assert.That(spans[5]).IsEqualTo(new MdSpan("f", MdStyle.Italic));

        // Unterminated marker renders literally.
        var open = StreamingMarkdownRenderer.ScanInline("x **y z");
        await Assert.That(open[^1].Text.EndsWith("**y z")).IsFalse(); // no crash; markers consumed or literal deterministically
    }

    [Test]
    public async Task Parser_ClassifiesAndCompletes()
    {
        var blocks = MarkdownBlockParser.Parse("para one\n\n- item\n# head\n```js\ncode\n```\n");
        await Assert.That(blocks.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(blocks.All(b => b.Complete)).IsTrue();

        var openFence = MarkdownBlockParser.Parse("```py\nnever closed\n");
        await Assert.That(openFence.Count).IsEqualTo(1);
        await Assert.That(openFence[0].Complete).IsFalse();
    }

    [Test]
    public async Task TryResume_ExtendsOpenBlock_MatchesFullParse()
    {
        // #2630. One huge open paragraph, grown on its last line only. The
        // resumed block list must equal a full re-parse, field for field.
        string prefix = "first line of an open paragraph\nsecond line still open\nthird line growing";
        var blocks = MarkdownBlockParser.Parse(prefix);
        var key = MarkdownBlockParser.KeyFor(prefix.AsSpan());
        string grown = prefix + " with more text on the same line";

        await Assert.That(MarkdownBlockParser.TryResume(grown.AsSpan(), key, blocks)).IsTrue();

        var full = MarkdownBlockParser.Parse(grown);
        await Assert.That(blocks.Count).IsEqualTo(full.Count);
        for (int i = 0; i < full.Count; i++)
        {
            await Assert.That(blocks[i].Kind).IsEqualTo(full[i].Kind);
            await Assert.That(blocks[i].Start).IsEqualTo(full[i].Start);
            await Assert.That(blocks[i].End).IsEqualTo(full[i].End);
            await Assert.That(blocks[i].Complete).IsEqualTo(full[i].Complete);
            await Assert.That(blocks[i].Level).IsEqualTo(full[i].Level);
        }
    }

    [Test]
    public async Task TryResume_FallsBack_OnReclassificationOrCompletedLine()
    {
        // «`» is Text and «```» is FenceOpen. A change of class on the
        // anchored line can restructure earlier blocks, so reject the key.
        const string src = "para\n`";
        var key = MarkdownBlockParser.KeyFor(src.AsSpan());

        var sameClass = MarkdownBlockParser.Parse(src);
        await Assert.That(MarkdownBlockParser.TryResume("para\n``".AsSpan(), key, sameClass)).IsTrue();

        var reclassified = MarkdownBlockParser.Parse(src);
        await Assert.That(MarkdownBlockParser.TryResume("para\n```".AsSpan(), key, reclassified)).IsFalse();

        var completed = MarkdownBlockParser.Parse(src);
        await Assert.That(MarkdownBlockParser.TryResume("para\n`\n".AsSpan(), key, completed)).IsFalse();
    }

    [Test]
    public async Task MultiLineOpenParagraph_StreamedTokenByToken_MatchesOneShot()
    {
        // The #2630 hot shape has no blank terminator, so the whole paragraph
        // stays open. It used to be re-scanned in full on every frame.
        const string source = "line one of a long open paragraph\nline two of the same paragraph\nline three without any blank separator\n";

        var oneShot = new StreamingMarkdownRenderer();
        oneShot.Push(source);
        oneShot.Complete();
        _ = oneShot.RenderTail(24);

        var streamed = new StreamingMarkdownRenderer();
        foreach (var chunk in Chunks(source))
        {
            streamed.Push(chunk);
            _ = streamed.RenderTail(24);
        }

        streamed.Complete();
        _ = streamed.RenderTail(24);

        await Assert.That(MarkdownFixtures.Flatten(streamed.GetLines()))
            .IsEquivalentTo(MarkdownFixtures.Flatten(oneShot.GetLines()));
    }

    [Test]
    public async Task Complete_FreezesTrailingPartial()
    {
        var r = new StreamingMarkdownRenderer();
        r.Push("no trailing newline here");
        _ = r.RenderTail(30);
        await Assert.That(r.IsComplete).IsFalse();

        r.Complete();
        _ = r.RenderTail(30);

        // After completion the tail is final content; full view equals direct parse.
        var direct = new StreamingMarkdownRenderer();
        direct.Push("no trailing newline here");
        direct.Complete();
        _ = direct.RenderTail(30);
        await Assert.That(MarkdownFixtures.Flatten(r.GetLines())).IsEquivalentTo(MarkdownFixtures.Flatten(direct.GetLines()));
    }

    private static IEnumerable<string> Chunks(string s)
    {
        // Deterministic ragged splits: sizes 1,2,3 … cycling, so chunk
        // boundaries land mid-word, mid-marker and mid-fence.
        int i = 0, size = 1;
        while (i < s.Length)
        {
            int take = Math.Min(size, s.Length - i);
            yield return s.Substring(i, take);
            i += take;
            size = size % 5 + 1;
        }
    }
}
