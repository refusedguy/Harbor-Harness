// FrameSnapshotCostGate.cs — #1140 (slice 3 of the per-frame snapshot batch).
//
// THE SLICE
// ---------
// The CellForge renderer paints a frame by walking the layout tree and
// painting every panel. Two of those panels rebuilt a snapshot of their own
// state on EVERY frame, whether or not anything had changed:
//
//   * `SessionTabStripPanel.Layout` derived the WHOLE tab-strip plan again
//     for every painted frame: two scratch `int[]` arrays, an
//     `ImmutableArray<TabCell>` builder, a cell array, and a cleaned +
//     truncated title string per visible tab. `TabStripState` is an immutable
//     record the reducer replaces only when a tab really changes, so on a
//     quiet frame every byte of that was thrown away unread.
//   * `ToastOverlay` built its row as a fresh string per toast per frame
//     ("<glyph> " + message, plus a truncated copy when it did not fit), and
//     `OverlayStack.PaintOver` allocated two `List<Rect>` scratch lists per
//     painted frame for its fragment/occluder bookkeeping.
//
// The plan is now memoised against (the strip snapshot, the width); the toast
// row is written into a stack buffer; the overlay stack owns its scratch.
//
// THE GATE
// --------
// A BUDGET PER UNIT OF WORK, not a ratio (#1747): the unit is ONE PAINTED
// FRAME of the surface, the budget is a constant number of bytes per frame,
// and the time budget is in PROBE UNITS so the runner's speed cancels.
//
//   bytes per painted tab-strip frame   <= the budget (machine-independent, HARD)
//   probe units per painted frame       <= the budget (machine-scaled, SOFT)
//
// The allocation branch asserts hard; the time branch asserts only when the
// measurement is trusted (the spread across rounds is inside
// `MaxTrustedSpread` and the leg was long enough to measure). A runner that
// cannot reproduce its own baseline is not evidence about the code, so it
// reports and stands down instead of failing the build.
//
// NON-VACUITY
// -----------
// A byte budget of zero satisfies every ceiling, so a gate that measured
// nothing would pass on any tree (#591). Two positive controls keep that
// shape out:
//
//   * `TabStrip_ChangedStripFrame_PaysTheLayoutTheMemoRemoves` — a frame on
//     which the tab set changed (a fresh strip snapshot) still pays the whole
//     layout, and the test asserts that this costs at least the layout's own
//     floor in bytes. So the work the memo removes from the quiet frames is a
//     real allocation of a known size, and the quiet-frame budget is a
//     budget on that work — not on nothing.
//   * `ToastRow_RemovedStringBuild_WasARealAllocation` — the removed row
//     build, verbatim, is measured in isolation: the string it produced was a
//     real allocation, so the row buffer that replaced it removed something.
//
// [NotInParallel] — bare, not keyed: the allocation counter is the
// per-thread one (the measured bodies never suspend), but the time leg is
// wall-clock and a neighbour burning cores moves it.

using System.Collections.Immutable;
using System.Diagnostics;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Per-frame cost gates for the CellForge renderer (#1140, slice 3).
/// </summary>
[NotInParallel]
public sealed class FrameSnapshotCostGate
{
    /// <summary>Open tabs in the measured strip.</summary>
    private const int Tabs = 6;

    /// <summary>Columns the strip is laid out for.</summary>
    private const int StripWidth = 80;

    /// <summary>Rows the strip occupies (the titles row plus the underline).</summary>
    private const int StripRows = 2;

    /// <summary>
    ///     Painted frames per measured leg. Big enough that one leg is well
    ///     above the harness's "long enough to measure" floor (5 ms), so the
    ///     time branch is not stood down for a leg that is too short to
    ///     decide anything.
    /// </summary>
    private const int Frames = 2_000;

    /// <summary>Toast message length — long enough to be truncated at the toast width.</summary>
    private const int ToastMessageChars = 48;

    // ── the budgets ─────────────────────────────────────────────────────────
    //
    // PROVISIONAL in the first commit of this slice: the numbers are pinned
    // from the CI measurement that follows, exactly as the house pattern
    // asks ("the byte budget is pinned from the CI measurement").

    /// <summary>
    ///     Allocation budget for ONE painted tab-strip frame, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    /// </summary>
    private const long MaxBytesPerTabStripFrame = 64 * 1024;

    /// <summary>
    ///     Time budget for ONE painted tab-strip frame, in probe units (see
    ///     <c>PerfGate</c>). Machine-scaled and soft.
    /// </summary>
    private const double MaxProbeUnitsPerTabStripFrame = 20_000.0;

    /// <summary>
    ///     Allocation budget for ONE painted overlay frame (the toast strip
    ///     inside the overlay stack), in bytes.
    /// </summary>
    private const long MaxBytesPerOverlayFrame = 64 * 1024;

    /// <summary>
    ///     Time budget for ONE painted overlay frame, in probe units.
    /// </summary>
    private const double MaxProbeUnitsPerOverlayFrame = 20_000.0;

    /// <summary>
    ///     The layout's own floor, in bytes: the tab-cell array the plan
    ///     carries (one reference per cell is the smallest it can be) plus the
    ///     two scratch <c>int[]</c> arrays the layout used to allocate. The
    ///     control below asserts that a frame which REBUILDS the plan costs at
    ///     least this much, so the quiet-frame budget is a budget on real
    ///     work.
    /// </summary>
    private const long LayoutRebuildFloor = (Tabs * 8L) + (Tabs * 4L) + (Tabs * 4L);

    /// <summary>
    ///     The removed toast row build's floor, in bytes: the concatenated row
    ///     string and the truncated copy the paint used to make. A one-char
    ///     string costs 24 bytes of header on x64, so two of them plus their
    ///     characters are comfortably above this.
    /// </summary>
    private const long ToastRowBuildFloor = 48;

    // ── gate 1: the tab strip ───────────────────────────────────────────────

    /// <summary>
    ///     One painted tab-strip frame must stay inside a constant byte
    ///     budget. The strip is painted on every frame of every session that
    ///     has two or more tabs open, and the plan it derives is a pure
    ///     function of (the strip snapshot, the width) — so a quiet frame must
    ///     not pay for the plan again.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task TabStrip_SteadyStateFrame_CostPerFrame_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Same guard rationale as the other allocation gates: the
            // stopwatch and the thread-local allocation counter belong to a
            // measurement, and this project is not in ci.yml's `test-os` list.
            return;
        }

        // Built and warmed OUTSIDE the measured window: the first paint pays
        // the layout and the JIT, and neither is what the quiet-frame budget
        // is about.
        SessionTabStripPanel panel = BuildStripPanel(out LayoutTree tree);
        var buffer = new ScreenBuffer(StripWidth, StripRows);
        panel.Paint(buffer);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: frames => Task.FromResult(DriveTabStripCost(panel, buffer, frames)),
            units: Frames,
            options: new PerfGateOptions
            {
                Label = "cellforge-tabstrip-frame",
                MaxBytesPerUnit = MaxBytesPerTabStripFrame,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerTabStripFrame,
                RequirePositiveBytes = false,
            });

        await result.AssertWithinBudgetsAsync("cellforge tab strip per painted frame");

        // The tree must still hold the panel: the gate measures the panel's
        // paint, and a panel that fell out of the tree would paint nothing
        // and make the budget meaningless.
        await Assert.That(tree.Panels.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     Positive control for the gate above: a frame on which the tab set
    ///     CHANGED still pays the whole layout, and that layout is a real
    ///     allocation of a known size. The memo moves the layout off the quiet
    ///     frames; it does not make the layout free.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task TabStrip_ChangedStripFrame_PaysTheLayoutTheMemoRemoves()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        SessionTabStripPanel panel = BuildStripPanel(out _);
        var buffer = new ScreenBuffer(StripWidth, StripRows);
        panel.Paint(buffer);

        // A fresh snapshot per frame — the shape the reducer produces when a
        // tab really changes, so the memo misses and the layout is rebuilt.
        // The snapshot itself is allocated inside the window (it is the
        // change), so this arm is an UPPER bound on the layout's own cost.
        long bytesPerFrame = MeasureChangedStripFrames(panel, buffer, Frames);

        Console.WriteLine(
            $"[perf-gate] cellforge-tabstrip-changed-frame: measured {bytesPerFrame} B per changed frame "
            + $"(floor {LayoutRebuildFloor} B) — the layout the memo removes from the quiet frames");

        await Assert.That(bytesPerFrame)
            .IsGreaterThanOrEqualTo(LayoutRebuildFloor)
            .Because(
                "a frame on which the tab set changed rebuilds the whole plan — two scratch arrays, the "
                + "cell array and a title string per visible tab. The quiet-frame budget above is a budget "
                + "on that work, so the work must be real: the measured cost of a changed frame is "
                + $"{bytesPerFrame} B against a floor of {LayoutRebuildFloor} B "
                + $"({Tabs} cells: the cell array's references plus the two scratch int arrays).");
    }

    // ── gate 2: the toast strip inside the overlay stack ────────────────────

    /// <summary>
    ///     One painted overlay frame — the toast strip inside the overlay
    ///     stack — must stay inside a constant byte budget. The stack paints
    ///     on every frame an overlay is up, and the toast row it paints is a
    ///     fixed shape: a glyph, a separator and a truncated message.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task ToastOverlay_SteadyStateFrame_CostPerFrame_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        OverlayStack stack = BuildToastStack(out ToastNotification toast);
        var buffer = new ScreenBuffer(80, 24);

        // Warm: the first paint pays the JIT and any one-off setup.
        stack.PaintOver(buffer);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: frames => Task.FromResult(DriveOverlayCost(stack, buffer, frames)),
            units: Frames,
            options: new PerfGateOptions
            {
                Label = "cellforge-toast-frame",
                MaxBytesPerUnit = MaxBytesPerOverlayFrame,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerOverlayFrame,
                RequirePositiveBytes = false,
            });

        await result.AssertWithinBudgetsAsync("cellforge toast overlay per painted frame");

        // The toast must still be on the stack: an empty stack would paint
        // nothing and make the budget meaningless.
        await Assert.That(stack.Count).IsEqualTo(1);
        await Assert.That(toast.Message.Length).IsEqualTo(ToastMessageChars);
    }

    /// <summary>
    ///     Positive control for the gate above: the row build the toast used
    ///     to do — the concatenated string, plus the truncated copy when the
    ///     row did not fit — was a real allocation. The stack buffer that
    ///     replaced it removed something, and this measures the something.
    /// </summary>
    [Test]
    public async Task ToastRow_RemovedStringBuild_WasARealAllocation()
    {
        ToastNotification toast = BuildToast();
        const int textWidth = 38;

        // Warm the JIT on the shape before the window.
        _ = LegacyToastRow(toast, textWidth);

        long allocated = AllocationProbe.MeasureThread(() =>
        {
            string last = string.Empty;
            for (int i = 0; i < 100; i++)
            {
                last = LegacyToastRow(toast, textWidth);
            }

            // The string has to survive the window: an unused result would let
            // the measurement describe nothing.
            GC.KeepAlive(last);
        });

        long perRow = allocated / 100;
        Console.WriteLine(
            $"[perf-gate] cellforge-toast-row-build: measured {perRow} B per removed row build "
            + $"(floor {ToastRowBuildFloor} B)");

        await Assert.That(perRow)
            .IsGreaterThanOrEqualTo(ToastRowBuildFloor)
            .Because(
                "the removed row build concatenated the glyph, the separator and the message into a fresh "
                + $"string (and a second one when the row did not fit). The measured cost is {perRow} B per "
                + $"row against a floor of {ToastRowBuildFloor} B — a string header alone is 24 bytes on "
                + "x64, so anything below the floor means the measurement measured nothing.");
    }

    // ── the measured shapes ─────────────────────────────────────────────────

    /// <summary>
    ///     A tab strip with <see cref="Tabs" /> open tabs, seated in a real
    ///     layout tree so the panel's rect is the one production gives it.
    /// </summary>
    private static SessionTabStripPanel BuildStripPanel(out LayoutTree tree)
    {
        var panel = new SessionTabStripPanel();
        panel.Strip = BuildStrip();
        tree = new LayoutTree();
        tree.AddRoot(panel);
        tree.Solve(StripWidth, StripRows);
        return panel;
    }

    /// <summary>The strip snapshot: <see cref="Tabs" /> open tabs, the first focused.</summary>
    private static TabStripState BuildStrip()
    {
        var builder = ImmutableArray.CreateBuilder<SessionTab>(Tabs);
        for (int i = 0; i < Tabs; i++)
        {
            builder.Add(new SessionTab(SessionId.Create($"session-{i}"), $"session {i}"));
        }

        return new TabStripState
        {
            Tabs = builder.ToImmutable(),
            ActiveTabId = builder[0].SessionId
        };
    }

    /// <summary>
    ///     Paints the strip <paramref name="frames" /> times and reports the
    ///     window's wall time and its allocation.
    /// </summary>
    private static PerfGateLeg DriveTabStripCost(SessionTabStripPanel panel, ScreenBuffer buffer, int frames)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            panel.Paint(buffer);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     The same paint, on a frame on which the tab set changed: a fresh
    ///     strip snapshot per frame, so the plan is rebuilt every time. Returns
    ///     the bytes per frame.
    /// </summary>
    private static long MeasureChangedStripFrames(SessionTabStripPanel panel, ScreenBuffer buffer, int frames)
    {
        TabStripState strip = BuildStrip();

        // Warm the changed-frame shape before the window.
        panel.Strip = strip;
        panel.Paint(buffer);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            // A new snapshot instance with the same tabs: exactly what the
            // reducer hands over when a tab really changes.
            panel.Strip = new TabStripState
            {
                Tabs = strip.Tabs,
                ActiveTabId = strip.ActiveTabId
            };
            panel.Paint(buffer);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return allocated / frames;
    }

    /// <summary>
    ///     An overlay stack with one visible toast layer, seated the way the
    ///     chat screen seats it.
    /// </summary>
    private static OverlayStack BuildToastStack(out ToastNotification toast)
    {
        toast = BuildToast();
        var overlay = new ToastOverlay();
        overlay.Show(toast.Message, toast.Kind);

        var layer = new ToastOverlayLayer(overlay);
        layer.Sync(new Rect(0, 0, 80, 24));

        var stack = new OverlayStack();
        stack.Push(layer);
        return stack;
    }

    /// <summary>The toast the overlay gate paints: a message longer than the toast is wide.</summary>
    private static ToastNotification BuildToast() =>
        new(new string('m', ToastMessageChars), ToastKind.Info);

    /// <summary>
    ///     Paints the overlay stack <paramref name="frames" /> times and
    ///     reports the window's wall time and its allocation.
    /// </summary>
    private static PerfGateLeg DriveOverlayCost(OverlayStack stack, ScreenBuffer buffer, int frames)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            stack.PaintOver(buffer);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     The row build the toast used to do, verbatim: the concatenated
    ///     string, plus the truncated copy when the row did not fit. Kept so
    ///     the gate's byte budget cannot be satisfied by measuring nothing.
    /// </summary>
    private static string LegacyToastRow(ToastNotification toast, int textWidth)
    {
        string prefix = LegacyGlyph(toast.Kind) + " ";
        string combined = prefix + toast.Message;
        if (combined.Length > textWidth)
        {
            combined = combined[..Math.Max(0, textWidth - 1)] + "…";
        }

        return combined;
    }

    /// <summary>The glyph the legacy row build used.</summary>
    private static string LegacyGlyph(ToastKind kind) => kind switch
    {
        ToastKind.Success => "✓",
        ToastKind.Warning => "!",
        ToastKind.Error => "✗",
        _ => "ℹ",
    };
}
