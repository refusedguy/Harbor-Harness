using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2354: addressable subgraph invalidation. A single edited row must
/// produce a one-row frame hint (V2 hinted diff), never a full-screen scan,
/// and the store ledger must be able to narrow a conservative broad mark to
/// the one view a mutation actually touched.
/// </summary>
public class ViewDamageTrackerTests
{
    private const int Cols = 100;
    private const int Rows = 30;

    private static EngineCells.ViewDamageTracker MakeTracker()
    {
        var tracker = new EngineCells.ViewDamageTracker();
        tracker.SetViewport(Cols, Rows);
        tracker.Register("chat.timeline", new EngineCells.Rect(0, 0, Cols, 28));
        tracker.Register("chat.composer", new EngineCells.Rect(0, 28, Cols, 1));
        tracker.Register("chat.status", new EngineCells.Rect(0, 29, Cols, 1));
        return tracker;
    }

    [Test]
    public async Task One_Row_Of_Damage_Stays_A_One_Row_Hint()
    {
        var tracker = MakeTracker();
        tracker.Damage("chat.composer");

        var hints = new EngineCells.Rect[16];
        bool broad = tracker.Drain(hints, out int count);

        await Assert.That(broad).IsFalse();
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(hints[0]).IsEqualTo(new EngineCells.Rect(0, 28, Cols, 1));
        await Assert.That(tracker.PendingCount).IsEqualTo(0);

        // 100 of 3000 cells (3.3%) is far under the canonical 25 % budget:
        // the engine takes the hinted path, not the fused full scan.
        var engine = new EngineCells.DiffEngine(Cols, Rows);
        engine.FrameHint(hints[0]);
        await Assert.That(engine.HintArea()).IsEqualTo(Cols);
        await Assert.That(engine.HintArea() * 4).IsLessThan((long)Cols * Rows);
    }

    [Test]
    public async Task Hinted_Diff_Confines_Changes_To_The_Damaged_Row()
    {
        var prev = new UIR.ScreenBuffer(Cols, Rows);
        var next = new UIR.ScreenBuffer(Cols, Rows);
        for (int y = 0; y < Rows; y++)
        {
            prev.SetText(0, y, new string('a', Cols), UIR.CellStyle.Plain);
            next.SetText(0, y, new string('a', Cols), UIR.CellStyle.Plain);
        }

        // The only mutation in the frame is on composer row 28.
        next.SetText(0, 28, new string('b', Cols), UIR.CellStyle.Plain);

        var hints = new[] { new UIR.Rect(0, 28, Cols, 1) };
        var batch = new RowHashDiffEncoder().Encode(prev, next, hints, sequence: 7);

        await Assert.That(batch.Version).IsEqualTo(CellDiffProtocolVersion.V2);
        await Assert.That(batch.FrameHints.Length).IsEqualTo(1);
        await Assert.That(batch.FrameHints[0]).IsEqualTo(hints[0]);
        await Assert.That(batch.Changes.Length).IsEqualTo(Cols);
        foreach (var change in batch.Changes)
        {
            await Assert.That(change.Y).IsEqualTo(28);
        }
    }

    [Test]
    public async Task Damage_By_Rect_Is_Addressable_And_Narrow()
    {
        var tracker = MakeTracker();
        tracker.Damage(new EngineCells.Rect(4, 28, 10, 1));

        var hints = new EngineCells.Rect[16];
        bool broad = tracker.Drain(hints, out int count);

        await Assert.That(broad).IsFalse();
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(hints[0]).IsEqualTo(new EngineCells.Rect(4, 28, 10, 1));
    }

    [Test]
    public async Task Broad_Mark_Keeps_The_Full_Scan_Escape_Hatch()
    {
        var tracker = MakeTracker();
        tracker.DamageAll();
        tracker.Damage("chat.composer");

        var hints = new EngineCells.Rect[16];
        bool broad = tracker.Drain(hints, out int count);

        await Assert.That(broad).IsTrue();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(tracker.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task Store_Ledger_Narrows_Broad_Input_Mark_To_One_View()
    {
        var store = new UiStore();
        store.DamageAll(); // conservative mark on input entry
        store.Damage("chat.status"); // another mark from a prior frame
        await Assert.That(store.DamageLedger.PendingCount).IsEqualTo(2);

        // A plain composer edit narrows both marks to the one touched view.
        store.NarrowDamageTo("chat.composer");
        await Assert.That(store.DamageLedger.PendingCount).IsEqualTo(1);

        var ids = new List<string>();
        var rects = new List<UIR.Rect>();
        bool broad = store.DamageLedger.DrainInto(ids, rects);

        await Assert.That(broad).IsFalse();
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0]).IsEqualTo("chat.composer");
        await Assert.That(rects.Count).IsEqualTo(0);
        await Assert.That(store.DamageLedger.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task Store_Ledger_Broad_Mark_Swallows_Precise_Ones()
    {
        var store = new UiStore();
        store.Damage("chat.composer");
        store.Damage(new UIR.Rect(0, 5, 10, 2));
        store.DamageAll();

        var ids = new List<string>();
        var rects = new List<UIR.Rect>();
        bool broad = store.DamageLedger.DrainInto(ids, rects);

        await Assert.That(broad).IsTrue();
        await Assert.That(ids.Count).IsEqualTo(0);
        await Assert.That(rects.Count).IsEqualTo(0);
    }
}
