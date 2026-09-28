using System.Text;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class TreeViewTests
{
    private static TreeView Sample() => new(
    [
        new TreeNode("src",
        [
            new TreeNode("app.cs"),
            new TreeNode("lib", [new TreeNode("x.cs")]),
        ], expanded: true),
        new TreeNode("README.md"),
    ]);

    private static KeyEvent Release(KeyCode key) =>
        new(key, default, KeyModifiers.None, KeyEventType.Release, false);

    [Test]
    public async Task Measure_CollapsedForest_CountsVisibleRows()
    {
        var tree = Sample(); // src, app.cs, lib (collapsed), README.md
        await Assert.That(tree.VisibleCount).IsEqualTo(4);
        await Assert.That(tree.Measure(40).MinLines).IsEqualTo(4);
        await Assert.That(tree.Measure(40).IsExact).IsTrue();
    }

    [Test]
    public async Task Toggle_Enter_ExpandsAndCollapsesParent()
    {
        var tree = Sample();
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down));
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down)); // lib
        await Assert.That(tree.CursorIndex).IsEqualTo(2);

        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(tree.VisibleCount).IsEqualTo(5);
        await Assert.That(tree.Measure(40).MinLines).IsEqualTo(5);

        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(tree.VisibleCount).IsEqualTo(4);
    }

    [Test]
    public async Task Toggle_Space_MatchesEnter()
    {
        var tree = Sample();
        var space = KeyEvent.Char(new Rune(' '));
        await Assert.That(tree.HandleKey(space)).IsTrue(); // src collapses
        await Assert.That(tree.VisibleCount).IsEqualTo(2);
        await Assert.That(tree.HandleKey(space)).IsTrue();
        await Assert.That(tree.VisibleCount).IsEqualTo(4);
    }

    [Test]
    public async Task Toggle_Leaf_NotConsumed()
    {
        var tree = Sample();
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down)); // app.cs leaf
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(tree.HandleKey(KeyEvent.Char(new Rune(' ')))).IsFalse();
        await Assert.That(tree.VisibleCount).IsEqualTo(4);
    }

    [Test]
    public async Task Nav_UpDown_ClampsAtEdges()
    {
        var tree = Sample();
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsTrue();
        await Assert.That(tree.CursorIndex).IsEqualTo(0);

        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(tree.CursorIndex).IsEqualTo(3);
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(tree.CursorIndex).IsEqualTo(3);
    }

    [Test]
    public async Task Nav_Left_CollapsesThenAscends()
    {
        var tree = Sample();
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue(); // collapse src
        await Assert.That(tree.VisibleCount).IsEqualTo(2);
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsFalse(); // root, nothing above

        // Descend into lib, then Left ascends back to lib (already collapsed).
        var open = Sample();
        _ = open.HandleKey(KeyEvent.Simple(KeyCode.Down));
        _ = open.HandleKey(KeyEvent.Simple(KeyCode.Down));
        _ = open.HandleKey(KeyEvent.Simple(KeyCode.Enter)); // expand lib
        _ = open.HandleKey(KeyEvent.Simple(KeyCode.Down)); // x.cs
        await Assert.That(open.CursorIndex).IsEqualTo(3);
        await Assert.That(open.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(open.CursorIndex).IsEqualTo(2);
    }

    [Test]
    public async Task Nav_Right_ExpandsThenDescends()
    {
        var tree = Sample();
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down));
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down)); // lib, collapsed
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(tree.VisibleCount).IsEqualTo(5);
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue(); // descend to x.cs
        await Assert.That(tree.CursorIndex).IsEqualTo(3);
    }

    [Test]
    public async Task Nav_Right_OnLeaf_NotConsumed()
    {
        var tree = Sample();
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.End)); // README.md leaf
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsFalse();
    }

    [Test]
    public async Task Nav_HomeEnd_Jump()
    {
        var tree = Sample();
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(tree.CursorIndex).IsEqualTo(3);
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Home));
        await Assert.That(tree.CursorIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Nav_PageUpDown_MoveViewport()
    {
        var tree = Sample();
        var buffer = new ScreenBuffer(40, 4);
        tree.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 4), 0));

        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.PageUp))).IsTrue();
        await Assert.That(tree.CursorIndex).IsEqualTo(0);
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.PageDown))).IsTrue();
        await Assert.That(tree.CursorIndex).IsEqualTo(3);
    }

    [Test]
    public async Task Nav_VimKeys_MirrorArrows()
    {
        var tree = Sample();
        _ = tree.HandleKey(KeyEvent.Char(new Rune('j')));
        await Assert.That(tree.CursorIndex).IsEqualTo(1);
        _ = tree.HandleKey(KeyEvent.Char(new Rune('k')));
        await Assert.That(tree.CursorIndex).IsEqualTo(0);
        await Assert.That(tree.HandleKey(KeyEvent.Char(new Rune('l')))).IsTrue(); // src expanded -> descend
        await Assert.That(tree.CursorIndex).IsEqualTo(1);
    }

    [Test]
    public async Task Nav_VimH_CollapsesLikeLeft()
    {
        var tree = Sample();
        await Assert.That(tree.HandleKey(KeyEvent.Char(new Rune('h')))).IsTrue();
        await Assert.That(tree.VisibleCount).IsEqualTo(2);
    }

    [Test]
    public async Task HandleKey_Ignores_ModifiersReleaseAndUnknown()
    {
        var tree = Sample();
        await Assert.That(tree.HandleKey(KeyEvent.Char(new Rune('j'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(tree.HandleKey(Release(KeyCode.Down))).IsFalse();
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsFalse();
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsFalse();
        await Assert.That(tree.HandleKey(KeyEvent.Char(new Rune('z')))).IsFalse();
        await Assert.That(tree.CursorIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_MeasuresOne_PaintsPlaceholder()
    {
        var tree = new TreeView();
        await Assert.That(tree.VisibleCount).IsEqualTo(0);
        await Assert.That(tree.Measure(40).MinLines).IsEqualTo(1);
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsFalse();

        var buffer = new ScreenBuffer(20, 1);
        tree.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 20, 1), 0));
        await Assert.That(GridDump.Art(buffer)).Contains("(empty)");
        await Assert.That(tree.RawText()).IsEqualTo("(empty)");
    }

    [Test]
    public async Task Paint_RendersGlyphsAndIndent()
    {
        var tree = Sample();
        var buffer = new ScreenBuffer(40, 4);
        tree.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 4), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("▾ src");
        await Assert.That(art).Contains("  · app.cs");
        await Assert.That(art).Contains("  ▸ lib");
        await Assert.That(art).Contains("· README.md");
        await Assert.That(tree.LastPaintRect is not null).IsTrue();
    }

    [Test]
    public async Task RawText_IndentedPreOrderList()
    {
        var tree = Sample();
        await Assert.That(tree.RawText()).IsEqualTo("▾ src\n  · app.cs\n  ▸ lib\n· README.md");
    }

    [Test]
    public async Task Changed_Fires_OnMoveAndToggle()
    {
        var tree = Sample();
        int fired = 0;
        tree.Changed += (_, _) => fired++;

        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down));
        await Assert.That(fired).IsEqualTo(1);

        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down)); // lib
        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Enter)); // expand
        await Assert.That(fired).IsEqualTo(3);

        _ = tree.HandleKey(KeyEvent.Simple(KeyCode.Down)); // x.cs leaf
        await Assert.That(tree.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(fired).IsEqualTo(4);
    }

    [Test]
    public async Task ExpandAll_CollapseAll_CoverForest()
    {
        var tree = Sample();
        tree.ExpandAll();
        await Assert.That(tree.VisibleCount).IsEqualTo(5);

        tree.CollapseAll();
        await Assert.That(tree.VisibleCount).IsEqualTo(2);
        await Assert.That(tree.CursorIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Focus_Id_UniquePerInstance()
    {
        var a = Sample();
        var b = Sample();
        await Assert.That(a.Id).IsNotEqualTo(b.Id);
        await Assert.That(a.Focused).IsFalse();
        a.OnFocusChanged(true);
        await Assert.That(a.Focused).IsTrue();
        await Assert.That(b.Focused).IsFalse();
    }

    [Test]
    public async Task Contract_KindBudgetAndStreamFlag()
    {
        var tree = Sample();
        await Assert.That(tree.Kind).IsEqualTo("tree");
        await Assert.That(tree.IsStreamContinuation).IsFalse();
        await Assert.That(tree.BudgetBytes).IsGreaterThan(0);
        await Assert.That(tree.CheapEstimate(40)).IsGreaterThan(0);
    }
}
