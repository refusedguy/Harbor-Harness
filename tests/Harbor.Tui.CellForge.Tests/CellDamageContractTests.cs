using Harbor.TestKit;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using Harbor.Ui.Framework.Rendering.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

using EngineCells = Harbor.Tui.CellForge.Rendering;
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2220: one damage contract joins widget invalidation to the
///     diff's hint path, one threshold governs the hinted/full decision, and
///     the decision's hit/miss outcome is visible in both diagnostics
///     snapshots. Counts are taken over explicit windows because the
///     underlying counters are opt-in and process-global (#409).
/// </summary>
/// <remarks>
///     Bare <c>[NotInParallel]</c> is load-bearing, as in
///     <c>UiStageCounterTests</c>: the counters are process-global, and my
///     <c>Reset</c> would erase a neighbouring class's in-flight window.
///     Keyed exclusion would only serialize tests sharing that key.
/// </remarks>
[NotInParallel]
public class CellDamageContractTests
{
    private static (EngineCells.DiffEngine Engine, RecordingBackend Backend, EngineCells.AnsiWriter Writer) MakeEngine(
        int cols, int rows)
    {
        var backend = new RecordingBackend();
        var writer = new EngineCells.AnsiWriter(backend, false);
        return (new EngineCells.DiffEngine(cols, rows), backend, writer);
    }

    private static UIR.ScreenBuffer MakeRenderingBuffer(int cols, int rows, char fill)
    {
        var buffer = new UIR.ScreenBuffer(cols, rows);
        for (int y = 0; y < rows; y++)
        {
            buffer.SetText(0, y, new string(fill, cols), UIR.CellStyle.Plain);
        }

        return buffer;
    }

    [Test]
    public async Task DirtyRect_Reaches_The_Hint_Ledger_Through_The_Contract()
    {
        var engine = new EngineCells.DiffEngine(10, 4);
        var dirty = new EngineCells.DirtyRect(10, 4);

        // A clean source makes no decision and registers no damage.
        engine.FrameHint(dirty);
        await Assert.That(engine.HintArea()).IsEqualTo(0);

        dirty.Invalidate(new EngineCells.Rect(2, 1, 3, 1));
        await Assert.That(dirty is EngineCells.IDamageRegion).IsTrue();
        await Assert.That(dirty.DirtyArea).IsEqualTo(3);

        engine.FrameHint(dirty);
        await Assert.That(engine.HintArea()).IsEqualTo(3);

        // The same contract supports viewport-local sources via translation.
        engine.ClearHints();
        engine.FrameHint(dirty, originX: 4, originY: 1);
        await Assert.That(engine.HintArea()).IsEqualTo(3);
    }

    [Test]
    public async Task Engine_Records_Exactly_One_Hit_Or_Miss_Per_Deciding_Frame()
    {
        var (engine, _, writer) = MakeEngine(20, 10);
        var next = new EngineCells.ScreenBuffer(20, 10);

        EngineCells.EngineStageCounters.Reset();
        EngineCells.EngineStageCounters.Enabled = true;
        try
        {
            // No hints: no decision, neither counter moves.
            writer.BeginFrame();
            engine.Flush(next, writer);
            await writer.EndFrameAsync();
            EngineCells.EngineStageSnapshot snap = EngineCells.EngineStageCounters.Snapshot();
            await Assert.That(snap.HintHits).IsEqualTo(0);
            await Assert.That(snap.HintMisses).IsEqualTo(0);

            // 4 cells of 200 is under the 25 % budget: a hit.
            engine.FrameHint(new EngineCells.Rect(0, 0, 4, 1));
            writer.BeginFrame();
            engine.Flush(next, writer);
            await writer.EndFrameAsync();
            snap = EngineCells.EngineStageCounters.Snapshot();
            await Assert.That(snap.HintHits).IsEqualTo(1);
            await Assert.That(snap.HintMisses).IsEqualTo(0);

            // Full-screen hints exceed the budget: one full-scan fallback miss.
            engine.FrameHint(new EngineCells.Rect(0, 0, 20, 10));
            writer.BeginFrame();
            engine.Flush(next, writer);
            await writer.EndFrameAsync();
            snap = EngineCells.EngineStageCounters.Snapshot();
            await Assert.That(snap.HintHits).IsEqualTo(1);
            await Assert.That(snap.HintMisses).IsEqualTo(1);
        }
        finally
        {
            EngineCells.EngineStageCounters.Enabled = false;
            EngineCells.EngineStageCounters.Reset();
        }
    }

    [Test]
    public async Task Portable_Encoder_Reports_The_Same_Hit_Miss_Outcome()
    {
        var prev = MakeRenderingBuffer(20, 10, 'a');
        var next = MakeRenderingBuffer(20, 10, 'a');
        var encoder = new RowHashDiffEncoder();

        UiStageCounters.Reset();
        UiStageCounters.Enabled = true;
        try
        {
            _ = encoder.Encode(prev, next, hints: null, sequence: 1);
            UiStageSnapshot snap = UiStageCounters.Snapshot();
            await Assert.That(snap.HintHits).IsEqualTo(0);
            await Assert.That(snap.HintMisses).IsEqualTo(0);

            _ = encoder.Encode(prev, next, hints: new[] { new UIR.Rect(0, 0, 4, 1) }, sequence: 2);
            snap = UiStageCounters.Snapshot();
            await Assert.That(snap.HintHits).IsEqualTo(1);
            await Assert.That(snap.HintMisses).IsEqualTo(0);

            _ = encoder.Encode(prev, next, hints: new[] { new UIR.Rect(0, 0, 20, 10) }, sequence: 3);
            snap = UiStageCounters.Snapshot();
            await Assert.That(snap.HintHits).IsEqualTo(1);
            await Assert.That(snap.HintMisses).IsEqualTo(1);
        }
        finally
        {
            UiStageCounters.Enabled = false;
            UiStageCounters.Reset();
        }
    }

    [Test]
    public async Task Single_Decision_Keeps_The_Golden_Boundary()
    {
        // 200 cells screen: the fallback starts exactly at the 25 % mark.
        await Assert.That(EngineCells.CellDamageContract.ShouldUseHints(49, 200)).IsTrue();
        await Assert.That(EngineCells.CellDamageContract.ShouldUseHints(50, 200)).IsFalse();
        await Assert.That(EngineCells.CellDamageContract.ShouldUseHints(0, 200)).IsTrue();
        await Assert.That(EngineCells.CellDamageContract.ShouldUseHints(1, 0)).IsFalse();
    }
}
