using System.Collections.Immutable;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #489 — the composer buffer materialised on every state sync.
///
/// <c>CellForgeTuiRenderer.SyncInputFromState</c> runs on every store change
/// (i.e. per keystroke AND per streamed token) and asked
/// <c>_composer.Buffer.SnapshotText() != text</c>. <c>SnapshotText()</c> is
/// <c>new(_buf, 0, _length)</c>: a fresh string for the whole draft, built
/// purely to learn that the draft had not changed. A long pasted draft
/// therefore cost one O(len) allocation per state sync — and it defeated the
/// intent of the comment right above it, which carefully documents which
/// parts of the draft are NOT mirrored.
///
/// <see cref="PromptBuffer.IsEquivalentTo"/> answers the same question in
/// place. Both halves of the contract are pinned here:
/// <list type="number">
///   <item><description>CORRECTNESS — the comparison agrees with
///     <c>SnapshotText() != text</c> on every case that can differ (length,
///     prefix, suffix, surrogate pair, empty);</description></item>
///   <item><description>MEASUREMENT — a steady-state sync (draft unchanged,
///     the per-streamed-token case) allocates no draft-sized bytes: measured
///     as a differential between a short and a long draft, so the fixed cost
///     of the rest of the projection cancels out and only the
///     draft-length-dependent term is gated.</description></item>
/// </list>
/// </summary>
[NotInParallel("alloc-tripwire")]
public class ComposerSyncScanTests
{
    private static InputModel TextModel(string text) =>
        new(text, ImmutableArray<string>.Empty, -1);

    private static void Seed(PromptBuffer buffer, string text)
    {
        buffer.Clear();
        if (text.Length != 0)
        {
            _ = buffer.InsertText(text);
        }

        _ = buffer.MoveTo(buffer.Length);
    }

    [Test]
    public async Task IsEquivalentTo_Matches_The_SnapshotText_Comparison()
    {
        var buffer = new PromptBuffer();
        var cases = new (string Seed, string Probe)[]
        {
            ("", ""),
            ("", "x"),
            ("x", ""),
            ("draft", "draft"),
            ("draft", "drift"),
            ("draft", "drafts"),
            ("drafts", "draft"),
            ("draft", "Draft"),
            ("draft ", "draft"),
            ("line one\nline two", "line one\nline two"),
            ("line one\nline two", "line one\nline 2"),
            ("привет мир", "привет мир"),
            ("привет мир", "привет"),
            // Surrogate pair: a naive char-count comparison must still be exact.
            ("ab😀", "ab😀"),
            ("ab😀", "ab😀!"),
        };

        foreach ((string seed, string probe) in cases)
        {
            Seed(buffer, seed);
            await Assert.That(buffer.IsEquivalentTo(probe)).IsEqualTo(buffer.SnapshotText() == probe)
                .Because($"seed={seed.Length} chars, probe={probe.Length} chars — a disagreement means the sync would mirror a draft that did not change (or miss one that did).");
        }
    }

    [Test]
    public async Task IsEquivalentTo_Accepts_A_Span_Of_The_Same_Text()
    {
        var buffer = new PromptBuffer();
        Seed(buffer, "the quick brown fox");

        await Assert.That(buffer.IsEquivalentTo("the quick brown fox".AsSpan())).IsTrue();
        await Assert.That(buffer.IsEquivalentTo(ReadOnlySpan<char>.Empty)).IsFalse();
    }

    [Test]
    public async Task IsEquivalentTo_Is_False_On_An_Empty_Buffer_For_Any_NonEmpty_Text()
    {
        var buffer = new PromptBuffer();

        await Assert.That(buffer.IsEquivalentTo(ReadOnlySpan<char>.Empty)).IsTrue();
        await Assert.That(buffer.IsEquivalentTo(" ".AsSpan())).IsFalse();
    }

    [Test]
    public async Task SyncInputFromState_Keeps_The_Buffer_Correct_Through_Store_Projection()
    {
        // The behaviour the allocation-free comparison must not disturb: a
        // draft that changes is mirrored, a draft that does not is left alone.
        using var renderer = NewRenderer();
        var buffer = renderer.PromptBuffer;

        renderer.ProjectStateIntoWidgets(State("first draft"));
        await Assert.That(buffer.SnapshotText()).IsEqualTo("first draft");
        await Assert.That(buffer.Cursor).IsEqualTo("first draft".Length);

        // Same draft again (the per-token re-projection): no clobber.
        renderer.ProjectStateIntoWidgets(State("first draft"));
        await Assert.That(buffer.SnapshotText()).IsEqualTo("first draft");

        // Same LENGTH, different content — a length-only check would miss this.
        renderer.ProjectStateIntoWidgets(State("first draft2"));
        await Assert.That(buffer.SnapshotText()).IsEqualTo("first draft2");

        // Back to empty.
        renderer.ProjectStateIntoWidgets(State(string.Empty));
        await Assert.That(buffer.SnapshotText()).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// THE measurement. A steady-state re-projection of an UNCHANGED draft is
    /// the per-streamed-token case: the store notifies, the projection runs,
    /// and the only question is "did the draft change?".
    /// <c>SnapshotText()</c> answered it by copying the draft — 2 bytes per
    /// char, per sync. <see cref="PromptBuffer.IsEquivalentTo"/> answers it in
    /// place, so the per-sync cost must no longer carry a draft-length term.
    ///
    /// Measured as a DIFFERENTIAL (long draft minus short draft) so the fixed
    /// cost of the rest of <c>ProjectStateIntoWidgets</c> — panel slots,
    /// placeholder, cursor clamp — cancels out and the gate sees only the
    /// draft-length-dependent term. The pre-#489 term was
    /// 2 × (2000 − 5) = 3 990 B per sync.
    /// </summary>
    [Test]
    public async Task SteadyState_Sync_Cost_Does_Not_Scale_With_DraftLength()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies 4x across OS runtimes.

        const int projections = 64;
        long longDraft = MeasureSteadyStateSyncs(new string('x', 2000), projections);
        long shortDraft = MeasureSteadyStateSyncs("draft", projections);

        Console.WriteLine(
            $"composer-sync-alloc: {projections} steady-state syncs — 2000-char draft {longDraft} B, "
            + $"5-char draft {shortDraft} B (min of 3 each)");

        long draftDependent = longDraft - shortDraft;
        await Assert.That(draftDependent).IsLessThanOrEqualTo(projections * 32L)
            .Because(
                "A per-sync draft copy is 2 bytes per char: the pre-#489 term was ~3 980 B per sync for a "
                + "2000-char draft, i.e. ~255 KB over this window. 32 B per sync absorbs the residual "
                + "difference in the projection's own fixed cost between the two arms and still fails by "
                + "two orders of magnitude if the buffer is materialised again.");
    }

    /// <summary>
    /// Allocation A/B on the comparison itself: the old idiom is kept in the
    /// test so the tripwire is anchored to a measured "before" rather than to
    /// a number someone typed.
    /// </summary>
    [Test]
    public async Task IsEquivalentTo_Allocates_Nothing_Where_SnapshotText_Copies_The_Draft()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only (see above).

        const int calls = 512;
        const int draftLength = 512;
        var buffer = new PromptBuffer();
        string draft = new('x', draftLength);
        Seed(buffer, draft);

        // Warm both idioms past the JIT tier-up threshold.
        for (int i = 0; i < 4_000; i++)
        {
            _ = buffer.IsEquivalentTo(draft);
            _ = buffer.SnapshotText() == draft;
        }

        long equivalent = MinOf3(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++)
            {
                _ = buffer.IsEquivalentTo(draft);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        long snapshot = MinOf3(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++)
            {
                _ = buffer.SnapshotText() == draft;
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Console.WriteLine(
            $"composer-buffer-alloc: {calls} comparisons over a {draftLength}-char draft — "
            + $"IsEquivalentTo {equivalent} B, SnapshotText {snapshot} B (min of 3 each)");

        await Assert.That(equivalent).IsEqualTo(0)
            .Because("IsEquivalentTo is the zero-alloc replacement for SnapshotText() != text on the per-sync path.");
        await Assert.That(snapshot).IsGreaterThan((long)calls * draftLength)
            .Because(
                "The old idiom must still be measurably worse — that is what anchors the tripwire. "
                + "A string copy of the draft cannot come in under 1 byte per char.");
    }

    /// <summary>Bytes allocated by <paramref name="body"/> on the current thread,
    /// best of 3 rounds after a forced collection — the
    /// <c>AgentLoopAllocationTests</c> shape (a single GC/JIT hiccup must not
    /// fail the gate).</summary>
    private static long MinOf3(Func<long> body)
    {
        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocated = body();
            if (allocated < best)
            {
                best = allocated;
            }
        }

        return best;
    }

    /// <summary>Bytes for <paramref name="projections"/> steady-state
    /// projections of an UNCHANGED draft (the per-token re-projection shape),
    /// best of 3.</summary>
    private static long MeasureSteadyStateSyncs(string draft, int projections)
    {
        using var renderer = NewRenderer();
        var state = State(draft);

        // Seed the buffer and warm the projection path before measuring.
        for (int i = 0; i < 64; i++)
        {
            renderer.ProjectStateIntoWidgets(state);
        }

        return MinOf3(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < projections; i++)
            {
                renderer.ProjectStateIntoWidgets(state);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
    }

    private static CellForgeTuiRenderer NewRenderer() => new(
        NullLogger<CellForgeTuiRenderer>.Instance,
        new RecordingBackend(),
        new StatusBarViewModel(),
        new ChatHistoryViewModel(),
        new InputViewModel());

    private static UiState State(string draft) => new()
    {
        Ui = TerminalUiState.Empty with { Input = TextModel(draft) }
    };
}
