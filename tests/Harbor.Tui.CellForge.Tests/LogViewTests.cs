using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Coverage for the generic log viewer (#2323): follow-tail autoscroll on a
/// large stream, filter, wrap, scrollback caps and the paint path. The tests
/// drive the primitive through its public API only.
/// </summary>
public class LogViewTests
{
    [Test]
    public async Task Append_WhileFollowing_SnapsToNewest()
    {
        var view = new LogView();
        view.Configure(20, 3);
        for (int i = 0; i < 10; i++)
        {
            view.Append("line " + i);
        }

        await Assert.That(view.FollowTail).IsTrue();
        await Assert.That(view.TotalRows).IsEqualTo(10L);
        await Assert.That(view.ScrollY).IsEqualTo(7L);
        await Assert.That(view.VisibleRows()).IsEquivalentTo(["line 7", "line 8", "line 9"]);
    }

    [Test]
    public async Task LargeStream_StaysBounded_AndAutoscrolls()
    {
        const int total = 100_000;
        var view = new LogView(budgetBytes: 4_000);
        view.Configure(40, 8);
        for (int i = 0; i < total; i++)
        {
            view.Append("L" + i);
        }

        await Assert.That(view.AppendedCount).IsEqualTo((long)total);
        await Assert.That(view.Count).IsGreaterThan(0);
        await Assert.That(view.Count).IsLessThan(200);
        await Assert.That(view.EvictedCount).IsEqualTo(total - view.Count);
        await Assert.That(view.FollowTail).IsTrue();
        await Assert.That(view.TotalRows).IsEqualTo(view.Count);
        await Assert.That(view.ScrollY).IsEqualTo(view.TotalRows - 8);
        await Assert.That(view.VisibleRows().Count).IsEqualTo(8);
        await Assert.That(view.VisibleRows()[^1]).IsEqualTo("L99999");
    }

    [Test]
    public async Task MaxLines_CapsScrollback_OfLargeStream()
    {
        var view = new LogView(budgetBytes: long.MaxValue, maxLines: 50);
        view.Configure(40, 5);
        for (int i = 0; i < 1_000; i++)
        {
            view.Append("L" + i);
        }

        await Assert.That(view.Count).IsEqualTo(50);
        await Assert.That(view.EvictedCount).IsEqualTo(950);
        await Assert.That(view.TotalRows).IsEqualTo(50L);
        await Assert.That(view.ScrollY).IsEqualTo(45L);
        await Assert.That(view.VisibleRows()[^1]).IsEqualTo("L999");
    }

    [Test]
    public async Task ScrollUp_UnpinsFollow_AndStopsAutoscroll()
    {
        var view = new LogView();
        view.Configure(40, 5);
        for (int i = 0; i < 20; i++)
        {
            view.Append("line " + i);
        }

        await Assert.That(view.ScrollY).IsEqualTo(15L);
        view.ScrollBy(-3);
        await Assert.That(view.FollowTail).IsFalse();
        await Assert.That(view.ScrollY).IsEqualTo(12L);

        for (int i = 0; i < 5; i++)
        {
            view.Append("late " + i);
        }

        await Assert.That(view.FollowTail).IsFalse();
        await Assert.That(view.ScrollY).IsEqualTo(12L);
        await Assert.That(view.TotalRows).IsEqualTo(25L);
    }

    [Test]
    public async Task ScrollToEnd_ReengagesFollow_AndResumesAutoscroll()
    {
        var view = new LogView();
        view.Configure(40, 5);
        for (int i = 0; i < 20; i++)
        {
            view.Append("line " + i);
        }

        view.ScrollToTop();
        await Assert.That(view.FollowTail).IsFalse();
        await Assert.That(view.ScrollY).IsEqualTo(0L);

        view.ScrollToEnd();
        await Assert.That(view.FollowTail).IsTrue();
        await Assert.That(view.ScrollY).IsEqualTo(15L);

        view.Append("tail");
        await Assert.That(view.ScrollY).IsEqualTo(16L);
        await Assert.That(view.VisibleRows()[^1]).IsEqualTo("tail");
    }

    [Test]
    public async Task Filter_KeepsOnlyMatches_AndFollowsFilteredTail()
    {
        var view = new LogView();
        view.Configure(40, 2);
        for (int i = 0; i < 10; i++)
        {
            view.Append(i % 2 == 0 ? "err " + i : "ok " + i);
        }

        view.SetFilter("err");
        await Assert.That(view.IsFiltering).IsTrue();
        await Assert.That(view.MatchedCount).IsEqualTo(5);
        await Assert.That(view.TotalRows).IsEqualTo(5L);
        await Assert.That(view.ScrollY).IsEqualTo(3L);
        await Assert.That(view.VisibleRows()).IsEquivalentTo(["err 6", "err 8"]);
    }

    [Test]
    public async Task Filter_CaseSensitive_SwitchesMatchSet()
    {
        var view = new LogView();
        view.Configure(40, 5);
        view.Append("ERROR upper");
        view.Append("error lower");

        view.SetFilter("error");
        await Assert.That(view.MatchedCount).IsEqualTo(2);

        view.CaseSensitive = true;
        await Assert.That(view.MatchedCount).IsEqualTo(1);
        await Assert.That(view.VisibleRows()).IsEquivalentTo(["error lower"]);
    }

    [Test]
    public async Task Filter_OverLargeStream_TracksFilteredTail()
    {
        const int total = 20_000;
        var view = new LogView();
        view.Configure(40, 4);
        for (int i = 0; i < total; i++)
        {
            view.Append(i % 1_000 == 0 ? "ERROR " + i : "info " + i);
        }

        view.SetFilter("ERROR");
        await Assert.That(view.MatchedCount).IsEqualTo(20);
        await Assert.That(view.TotalRows).IsEqualTo(20L);
        await Assert.That(view.ScrollY).IsEqualTo(16L);
        await Assert.That(view.VisibleRows()[^1]).IsEqualTo("ERROR 19000");
    }

    [Test]
    public async Task Filter_WithEviction_KeepsMatchedViewInSync()
    {
        var view = new LogView(budgetBytes: long.MaxValue, maxLines: 4);
        view.Configure(40, 5);
        for (int i = 0; i < 10; i++)
        {
            view.Append(i % 2 == 0 ? "err " + i : "ok " + i);
        }

        view.SetFilter("err");
        await Assert.That(view.Count).IsEqualTo(4);
        await Assert.That(view.MatchedCount).IsEqualTo(2);
        await Assert.That(view.TotalRows).IsEqualTo(2L);
        await Assert.That(view.VisibleRows()).IsEquivalentTo(["err 7", "err 9"]);
    }

    [Test]
    public async Task Wrap_On_RecomputesRows_AndKeepsTail()
    {
        var view = new LogView();
        view.Configure(10, 3);
        view.Append(new string('x', 25));

        await Assert.That(view.TotalRows).IsEqualTo(3L);
        await Assert.That(view.ScrollY).IsEqualTo(0L);
        await Assert.That(view.VisibleRows()).IsEquivalentTo(["xxxxxxxxxx", "xxxxxxxxxx", "xxxxx"]);
    }

    [Test]
    public async Task Wrap_Off_UsesOneRowPerLine()
    {
        var view = new LogView { Wrap = false };
        view.Configure(10, 3);
        view.Append(new string('x', 25));

        await Assert.That(view.TotalRows).IsEqualTo(1L);
        await Assert.That(view.VisibleRows()).IsEquivalentTo([new string('x', 25)]);
    }

    [Test]
    public async Task Configure_RewrapOnWidthChange_KeepsFollow()
    {
        var view = new LogView();
        view.Configure(40, 2);
        view.Append(new string('x', 25));
        await Assert.That(view.TotalRows).IsEqualTo(1L);
        await Assert.That(view.ScrollY).IsEqualTo(0L);

        view.Configure(10, 2);
        await Assert.That(view.TotalRows).IsEqualTo(3L);
        await Assert.That(view.ScrollY).IsEqualTo(1L);
        await Assert.That(view.FollowTail).IsTrue();
    }

    [Test]
    public async Task Paint_WritesOnlyTheVisibleTailIntoTheRect()
    {
        var view = new LogView();
        view.Configure(10, 2);
        view.Append("alpha");
        view.Append("beta");
        view.Append("gamma");

        var buffer = TestRender.Buffer(10, 2);
        view.Paint(buffer, new Rect(0, 0, 10, 2));
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('b');
        await Assert.That(buffer.Get(0, 1).Rune).IsEqualTo('g');
    }

    [Test]
    public async Task Paint_ClipsToRect_AndLeavesNeighboringCellsAlone()
    {
        var view = new LogView();
        view.Configure(10, 2);
        view.Append("alpha");
        view.Append("beta");
        view.Append("gamma");

        var buffer = TestRender.Buffer(10, 4);
        view.Paint(buffer, new Rect(2, 1, 4, 2));
        await Assert.That(buffer.Get(2, 1).Rune).IsEqualTo('b');
        await Assert.That(buffer.Get(2, 2).Rune).IsEqualTo('g');
        await Assert.That(buffer.Get(6, 2).Rune).IsEqualTo(' ');
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo(' ');
    }

    [Test]
    public async Task Clear_ResetsLinesAndFollow()
    {
        var view = new LogView();
        view.Configure(40, 3);
        view.Append("one");
        view.Append("two");
        view.ScrollBy(-1);

        view.Clear();
        await Assert.That(view.Count).IsEqualTo(0);
        await Assert.That(view.MatchedCount).IsEqualTo(0);
        await Assert.That(view.TotalRows).IsEqualTo(0L);
        await Assert.That(view.AppendedCount).IsEqualTo(0L);
        await Assert.That(view.FollowTail).IsTrue();
        await Assert.That(view.ScrollY).IsEqualTo(0L);
    }
}
