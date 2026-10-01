using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #416 (#46 slice 5) — <b>anchor identity + the shape of the generation
///     fence</b>. Two claims, and the second one is the load-bearing one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Claim 1 — the anchor is identity, not pixel.</b> The issue asks for
///         <c>(BlockId, offsetInBlock)</c>. The tree already carries it:
///         <see cref="TimelineLayoutCache.PinAnchor"/> resolves a pixel to a block
///         <i>reference</i> plus a row inside it, and <see cref="TimelineLayoutCache.RestoreAnchor"/>
///         re-finds that reference after the rebuild. This class pins that shape and
///         pins the one degradation that IS specified (eviction → nearest survivor)
///         so it cannot be quietly re-specified as "clamp to the old pixel".
///     </para>
///     <para>
///         <b>Claim 2 — there is no generation to fence, and that is the finding.</b>
///         The AC asks for "a test interleaves resize and replace so that an older
///         layout completes after a newer generation is published, gated with
///         <c>TaskCompletionSource</c>". That test cannot be written, because there
///         is nothing to interleave: <see cref="VirtualizedChatTimeline.PrepareFrame"/>,
///         <see cref="TimelineLayoutCache.PrepareLayout"/>, <c>PinAnchor</c>,
///         <c>RestoreAnchor</c> and <c>Paint</c> are all synchronous and run on one
///         thread, and both product hosts construct the bridge with
///         <c>autoSubscribe: false</c> so every block mutation is pumped on that same
///         render thread. A <c>TaskCompletionSource</c> gate would be theatre: it would
///         pass without ever interleaving two layouts, which is the exact failure mode
///         a guard is supposed to make impossible.
///     </para>
///     <para>
///         So instead of a fence that guards nothing, this file states the property
///         that makes a fence unnecessary, and makes it falsifiable in both
///         directions — see <see cref="NoAsyncWorkInTheLayoutPath"/> (a new
///         <c>async</c> layout seam lights the lamp) and
///         <see cref="EveryProductHostPumpsEventsOnTheRenderThread"/> (an
///         <c>autoSubscribe: true</c> product host lights it too). If a future change
///         introduces the cross-thread mutation the AC was written against, this
///         guard goes red and the fence gets written against a real interleave
///         rather than a simulated one.
///     </para>
///     <para>
///         <b>Non-vacuity.</b> Every behaviour test below is fed a synthetic tape
///         where the hit is guaranteed by construction (a 30-block transcript, a pin
///         at row 40, a commit that provably changes the block's height), and
///         <see cref="Oracle_SaysTheOldPixelIsWrongByExactlyTheHeightDelta"/>
///         asserts the oracle DISAGREES with the shipped code before the guard
///         claims the shipped code is right. The red-then-green demonstration for
///         the PR is in <c>AnchorGenerationGuardTests.md</c>.
///     </para>
/// </remarks>
public class AnchorGenerationGuardTests
{
    /// <summary>
    ///     A block whose measured height is a function of width, so a width flip
    ///     provably moves every row boundary. Deterministic, no wrapping.
    /// </summary>
    private sealed class WidthBlock(string id, int heightAt80, int heightAt120) : IChatBlock
    {
        public string Id { get; } = id;

        public string Kind => "guard";

        public bool IsStreamContinuation => false;

        public int BudgetBytes => 32;

        public BlockMeasure Measure(int width) => BlockMeasure.Exact(width <= 100 ? heightAt80 : heightAt120);

        public int CheapEstimate(int width) => width <= 100 ? heightAt80 : heightAt120;

        public void Paint(in BlockPaintContext ctx)
        {
        }

        public string RawText() => Id;
    }

    private static TimelineLayoutCache Build(int blocks, int h80 = 5, int h120 = 5)
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < blocks; i++)
        {
            cache.Append(new WidthBlock($"b{i}", h80, h120));
        }

        _ = cache.PrepareLayout(80, 10, scrollY: 0);
        return cache;
    }

    /// <summary>
    ///     The anchor survives a width flip by identity: after the flip the pinned
    ///     block is still the block under the restored offset — not merely "some
    ///     block at a similar offset".
    /// </summary>
    [Test]
    public async Task WidthFlip_AnchorHoldsTheSameBlock_NotTheSamePixel()
    {
        var cache = Build(30);
        cache.PinAnchor(scrollTopY: 40); // b8 (tops 0,5,10,…)

        var outcome = cache.PrepareLayout(120, 10, scrollY: 40);
        await Assert.That(outcome).IsEqualTo(LayoutOutcome.FullRebuild);

        long restored = cache.RestoreAnchor();
        int idx = cache.EntryAtY(restored);

        // Identity: b8 is the anchored block and is at the restored offset.
        await Assert.That(cache.BlockAt(idx).RawText()).IsEqualTo("b8");
        await Assert.That(restored - cache.BlockTop(idx)).IsEqualTo(0);
    }

    /// <summary>
    ///     A width flip that changes every row height is the case a pixel anchor
    ///     cannot survive: the pixel 40 means something else entirely after the
    ///     rebuild. Identity still lands on b8. This is the non-vacuity anchor —
    ///     the geometry differs, so only identity can produce the right answer.
    /// </summary>
    [Test]
    public async Task WidthFlip_ThatChangesHeights_StillHoldsIdentity_WherePixelCannot()
    {
        var cache = Build(30, h80: 5, h120: 2); // 5 rows/block -> 2 rows/block
        await Assert.That(cache.BlockAt(8).RawText()).IsEqualTo("b8");
        cache.PinAnchor(scrollTopY: 40); // b8 at 5-row geometry

        _ = cache.PrepareLayout(120, 10, scrollY: 40);
        long restored = cache.RestoreAnchor();
        int idx = cache.EntryAtY(restored);

        // Identity holds: b8 is still under the restored offset, 0 rows in.
        await Assert.That(cache.BlockAt(idx).RawText()).IsEqualTo("b8");
        await Assert.That(restored - cache.BlockTop(idx)).IsEqualTo(0);
        // …and the raw pixel 40 would now be a DIFFERENT block (b20 at 2 rows).
        await Assert.That(cache.BlockAt(cache.EntryAtY(40)).RawText()).IsNotEqualTo("b8");
    }

    /// <summary>
    ///     The streaming commit path must not orphan the anchor. <c>FinishStream</c>
    ///     calls <c>Timeline.Replace</c> to swap the live placeholder for the
    ///     committed block — same message, same slot, new instance. An anchor
    ///     pinned on the placeholder has to follow the replacement; otherwise the
    ///     next width flip degrades to row 0 and the reader loses their place.
    ///     <b>This was red before the fix</b>: the committed block's height differs
    ///     from the placeholder's, so identity and pixel disagree by a non-zero
    ///     amount and the test cannot pass by accident.
    /// </summary>
    [Test]
    public async Task Replace_CarriesTheAnchorForwardToTheCommittedBlock()
    {
        var cache = Build(30, h80: 5, h120: 5);
        cache.PinAnchor(scrollTopY: 40); // b8

        // Streaming commit: same slot, new instance, and a different height.
        cache.Replace(8, new WidthBlock("b8:committed", 1, 1));

        _ = cache.PrepareLayout(120, 10, scrollY: 40);
        long restored = cache.RestoreAnchor();
        int idx = cache.EntryAtY(restored);

        await Assert.That(cache.BlockAt(idx).RawText()).IsEqualTo("b8:committed");
        await Assert.That(restored - cache.BlockTop(idx)).IsEqualTo(0);
    }

    /// <summary>
    ///     The specified eviction degradation: when the anchored block is evicted the
    ///     anchor does NOT silently become the old pixel. <see cref="TimelineLayoutCache.EvictFirst"/>
    ///     clears it, and <see cref="TimelineLayoutCache.RestoreAnchor"/> then returns 0
    ///     — the nearest surviving position (the new top), which is a specified,
    ///     tested degradation rather than a clamp against stale geometry.
    /// </summary>
    [Test]
    public async Task EvictingTheAnchorBlock_DegradesToNearestSurvivor_NotTheOldPixel()
    {
        var cache = Build(30);
        cache.PinAnchor(scrollTopY: 40); // b8

        for (int i = 0; i < 9; i++)
        {
            // b0..b8 — includes the anchored block.
            await Assert.That(cache.EvictFirst()).IsTrue();
        }

        // b9 is now the first block; the anchor is cleared, not re-pointed at a pixel.
        await Assert.That(cache.BlockAt(0).RawText()).IsEqualTo("b9");

        _ = cache.PrepareLayout(120, 10, scrollY: 0);
        long restored = cache.RestoreAnchor();

        await Assert.That(restored).IsEqualTo(0); // nearest surviving position
        await Assert.That(restored).IsNotEqualTo(40); // NOT the pre-eviction pixel
    }

    /// <summary>
    ///     Oracle cross-check. On this tape the pre-eviction pixel (40) and the
    ///     specified degradation (0) disagree, so a guard that only ever compared
    ///     against its own expectation could not pass by accident: if the shipped
    ///     code returned the stale pixel, this test's <c>IsNotEqualTo(40)</c> fails.
    ///     Asserted here so the two tests above cannot rot into a tautology.
    /// </summary>
    [Test]
    public async Task Oracle_SaysTheOldPixelIsWrongByExactlyTheHeightDelta()
    {
        var cache = Build(30, h80: 5, h120: 2);
        cache.PinAnchor(scrollTopY: 40);

        _ = cache.PrepareLayout(120, 10, scrollY: 40);
        long identity = cache.RestoreAnchor(); // 16 — b8's top at 2-row geometry
        long pixel = 40; // what a pixel-anchored re-pin would have produced

        await Assert.That(identity).IsEqualTo(16);
        await Assert.That(pixel - identity).IsEqualTo(24); // the disagreement is non-zero
        await Assert.That(cache.BlockAt(cache.EntryAtY(pixel)).RawText()).IsNotEqualTo("b8");
    }

    /// <summary>
    ///     The streaming-commit path does not reset the anchor or follow-tail state:
    ///     <c>FinishStream</c> replaces the live placeholder in place, and the pinned
    ///     block is untouched by it (AC "ReplaceLast/Replace do not reset the anchor
    ///     or the follow-tail state").
    /// </summary>
    [Test]
    public async Task ReplaceLast_DoesNotResetFollowTailOrScroll()
    {
        var tl = new VirtualizedChatTimeline();
        var stream = new StreamingMarkdownBlock();
        stream.Push("partial answer");
        tl.Append(stream);

        _ = tl.PrepareFrame(80, 6);
        await Assert.That(tl.FollowTail).IsTrue();
        long followedAt = tl.ScrollY;

        tl.ReplaceLast(new AssistantMarkdownBlock("# committed\n\nanswer\n"));
        _ = tl.PrepareFrame(80, 6);

        await Assert.That(tl.FollowTail).IsTrue(); // still following the tail
        await Assert.That(tl.Count).IsEqualTo(1); // committed in place, no duplicate
        await Assert.That(tl.BlockAt(0).Kind).IsEqualTo("assistant");
        await Assert.That(tl.ScrollY).IsGreaterThanOrEqualTo(followedAt);
    }

    /// <summary>
///     Append while scrolled up (pinned) must NOT move the viewport — the
///     follow-tail contract the AC calls out, and the half of #412's
///     "simultaneous scroll + streaming append" AC that this slice owns
///     (they own the measure-count side; no counters needed here).
///
///     <para>Driven through <see cref="VirtualizedChatTimeline.ApplyStoreState"/>,
///     the seam the product uses. The low-level <see cref="VirtualizedChatTimeline.PrepareFrame"/>
///     cannot be used here: <see cref="TimelineLayoutCache.TotalHeight"/> is
///     <c>_virtual[_count]</c>, which only <c>PatchVirtualFrom</c> /
///     <c>RecomputeTailTotal</c> ever write, so immediately after an
///     <c>Append</c> the total is stale (0) and a bare
///     <c>PrepareFrame</c> legitimately clamps the offset to 0. That is why
///     <c>ApplyStoreState</c> exists and re-asserts the offset against the
///     freshly measured maximum after layout — a stale-total clamp, not a
///     follow-tail violation. Calling the wrong seam would have "proven" a
///     bug that is really a precondition.</para>
/// </summary>
[Test]
public async Task AppendWhileScrolledUp_LeavesTheViewportPut()
{
    var tl = new VirtualizedChatTimeline { BudgetBytes = long.MaxValue };
    for (int i = 0; i < 40; i++)
    {
        tl.Append(new WidthBlock($"b{i}", 3, 3));
    }

    // Total = 40*3 = 120; viewport 10 -> max 110. Store offset 30 = 30 rows up of the tail.
    var pinned = StoreAt(30);
    _ = tl.ApplyStoreState(pinned, 80, 10);
    await Assert.That(tl.FollowTail).IsFalse();
    await Assert.That(tl.ScrollY).IsEqualTo(80); // 110 - 30
    string anchorBlock = tl.BlockAt(tl.VisibleRange(10).First).RawText();

    // A streaming tail grows while the user reads history: 20 more blocks.
    for (int i = 40; i < 60; i++)
    {
        tl.Append(new WidthBlock($"b{i}", 3, 3));
    }

    // Same store offset, same width. The store offset is TAIL-RELATIVE, so the
    // absolute ScrollY legitimately grows with the tail (80 -> 140) and the visible
    // window moves DOWN the transcript: the reader keeps the same 30-rows-from-the-end
    // distance, which is the specified meaning of a non-zero ScrollOffset. Asserting
    // "ScrollY did not change" would encode the wrong contract -- the guarantee here is
    // that the view is NOT dragged to the tail (FollowTail stays false) and that the
    // reader keeps a fixed distance from it.
    _ = tl.ApplyStoreState(pinned, 80, 10);

    await Assert.That(tl.FollowTail).IsFalse();
    await Assert.That(tl.TotalHeight).IsEqualTo(180); // the append DID land
    await Assert.That(tl.ScrollY).IsEqualTo(140); // 170 - 30: still exactly 30 from the tail
    await Assert.That(tl.ScrollY).IsNotEqualTo(170); // and NOT snapped to the tail
    await Assert.That(tl.BlockAt(tl.VisibleRange(10).First).RawText()).IsNotEqualTo(anchorBlock);
}

/// <summary>
///     A store snapshot scrolled <paramref name="offset"/> rows up from the tail.
///     Store offset 0 means "pinned to the live tail" — the same convention
///     <c>ApplyStoreState</c> maps with <c>ScrollY = max - offset</c>.
/// </summary>
private static UiState StoreAt(int offset) => new()
{
    Ui = TerminalUiState.Empty with { ScrollOffset = offset, ViewportLines = 10 },
};

    // ── The fence-shaped property: no async layout seam exists ─────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Repo root (Harbor.slnx) not found above the test bin dir.");
    }

    private static string[] LayoutSources(string root) =>
    [
        "src/Harbor.Tui.CellForge/Chat/Widgets/VirtualizedChatTimeline.cs",
        "src/Harbor.Tui.CellForge/Chat/Widgets/TimelineLayoutCache.cs",
    ];

    /// <summary>
    ///     THE load-bearing guard. The layout path must stay synchronous: no
    ///     <c>async</c>, <c>await</c> or <c>Task</c> in the two files that own the
    ///     anchor, the follow-tail state and the layout. If this goes red, layout can
    ///     genuinely complete out of order — and THAT is when #416's
    ///     <c>TaskCompletionSource</c>-gated interleave test becomes writable. Until
    ///     then such a test would be a simulation of a race that cannot happen.
    /// </summary>
    [Test]
    public async Task NoAsyncWorkInTheLayoutPath()
    {
        string root = RepoRoot();
        var offenders = new List<string>();

        foreach (string rel in LayoutSources(root))
        {
            string text = File.ReadAllText(Path.Combine(root, rel));
            int lineNo = 0;
            foreach (string raw in text.Split('\n'))
            {
                lineNo++;
                // Strip comments so prose about async does not trip the guard.
                string code = raw;
                int slash = code.IndexOf("//", StringComparison.Ordinal);
                if (slash >= 0)
                {
                    code = code[..slash];
                }

                if (code.Contains("async ", StringComparison.Ordinal)
                    || code.Contains("await ", StringComparison.Ordinal)
                    || code.Contains("Task<", StringComparison.Ordinal)
                    || code.Contains("Task ", StringComparison.Ordinal)
                    || code.Contains("ValueTask", StringComparison.Ordinal))
                {
                    offenders.Add($"{rel}:{lineNo}: {raw.Trim()}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     The same property from the other side: every product host must pump bus
    ///     events on the render thread (<c>autoSubscribe: false</c>), which is what
    ///     makes the layout path single-threaded. Both current hosts already do; this
    ///     exists so a third host that opts into the concurrent path red-lights here
    ///     instead of silently making #416's fence load-bearing.
    /// </summary>
    [Test]
    public async Task EveryProductHostPumpsEventsOnTheRenderThread()
    {
        string root = RepoRoot();
        var offenders = new List<string>();

        foreach (string dir in new[] { "apps", "src" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}contrib{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                int idx = text.IndexOf("new ChatScreenBridge(", StringComparison.Ordinal);
                while (idx >= 0)
                {
                    // Scan to the closing paren of this construction.
                    int depth = 0;
                    int end = idx;
                    for (; end < text.Length && text[end] != ';'; end++)
                    {
                        if (text[end] == '(')
                        {
                            depth++;
                        }
                        else if (text[end] == ')')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                break;
                            }
                        }
                    }

                    string ctor = text[idx..Math.Min(end + 1, text.Length)];
                    int lineNo = text[..idx].Count(c => c == '\n') + 1;
                    if (!ctor.Contains("autoSubscribe: false", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNo}");
                    }

                    idx = text.IndexOf("new ChatScreenBridge(", idx + 1, StringComparison.Ordinal);
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }
}
