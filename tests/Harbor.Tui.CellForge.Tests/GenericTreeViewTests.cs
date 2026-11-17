using System.Collections.Immutable;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette — serialized with other paint tests.
[NotInParallel("pty")]
public class GenericTreeViewTests
{
    private static FileTreeSnapshot SampleSnapshot()
    {
        var xcs = new FileTreeEntry("x.cs", "/src/lib/x.cs", false, false);
        var lib = new FileTreeEntry("lib", "/src/lib", true, false, ImmutableArray.Create(xcs));
        var app = new FileTreeEntry("app.cs", "/src/app.cs", false, false);
        var src = new FileTreeEntry("src", "/src", true, false, ImmutableArray.Create(app, lib));
        var readme = new FileTreeEntry("README.md", "/README.md", false, false);
        return FileTreeSnapshot.Completed("/work", ImmutableArray.Create(src, readme));
    }

    [Test]
    public async Task FromSnapshot_MapsEntriesIntoNeutralNodes()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());

        await Assert.That(view.Roots.Count).IsEqualTo(2);
        await Assert.That(view.Roots[0].Label).IsEqualTo("src");
        await Assert.That(view.Roots[0].Key).IsEqualTo("/src");
        await Assert.That(view.Roots[0].IsDirectory).IsTrue();
        await Assert.That(view.Roots[0].Children.Count).IsEqualTo(2);
        await Assert.That(view.Roots[0].Children[1].Children[0].Key).IsEqualTo("/src/lib/x.cs");
        await Assert.That(view.Roots[1].Label).IsEqualTo("README.md");
        await Assert.That(view.Roots[1].HasChildren).IsFalse();
    }

    [Test]
    public async Task CollapsedForest_ShowsOnlyRoots()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());

        await Assert.That(view.VisibleCount).IsEqualTo(2);
        await Assert.That(view.SelectedNode?.Key).IsEqualTo("/src");
    }

    [Test]
    public async Task Enter_ExpandsAndCollapsesParentInPlace()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());
        _ = view.HandleKey(KeyEvent.Simple(KeyCode.Down)); // cursor on README.md

        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsTrue(); // back on src
        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(view.VisibleCount).IsEqualTo(4); // src, app.cs, lib, README.md
        await Assert.That(view.IsExpanded(view.Roots[0])).IsTrue();

        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(view.VisibleCount).IsEqualTo(2);
        await Assert.That(view.IsExpanded(view.Roots[0])).IsFalse();
    }

    [Test]
    public async Task Right_ExpandsThenDescends_Left_AscendsThenCollapses()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());

        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(view.VisibleCount).IsEqualTo(4); // expanded, cursor still on src
        await Assert.That(view.CursorIndex).IsEqualTo(0);
        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(view.CursorIndex).IsEqualTo(1); // first child, app.cs

        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(view.CursorIndex).IsEqualTo(0); // leaf ascends to parent src
        await Assert.That(view.VisibleCount).IsEqualTo(4);
        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(view.CursorIndex).IsEqualTo(0);
        await Assert.That(view.VisibleCount).IsEqualTo(2); // parent collapses
    }

    [Test]
    public async Task VimKeys_MoveCursorAndToggle()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());

        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('j')))).IsTrue();
        await Assert.That(view.CursorIndex).IsEqualTo(1);
        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('k')))).IsTrue();
        await Assert.That(view.CursorIndex).IsEqualTo(0);
        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('l')))).IsTrue();
        await Assert.That(view.VisibleCount).IsEqualTo(4);
        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('h')))).IsTrue();
        await Assert.That(view.VisibleCount).IsEqualTo(2);
    }

    [Test]
    public async Task ToggleLeaf_IsNotConsumed()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());
        _ = view.HandleKey(KeyEvent.Simple(KeyCode.Down)); // README.md, a leaf

        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(view.Toggle(view.Roots[1])).IsFalse();
        await Assert.That(view.VisibleCount).IsEqualTo(2);
    }

    [Test]
    public async Task ExpandAllAndCollapseAll_OperateOnWholeForest()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());

        view.ExpandAll();
        await Assert.That(view.VisibleCount).IsEqualTo(5); // src, app.cs, lib, x.cs, README.md
        view.CollapseAll();
        await Assert.That(view.VisibleCount).IsEqualTo(2);
    }

    [Test]
    public async Task SetSnapshot_PreservesExpansionByKey()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());
        _ = view.HandleKey(KeyEvent.Simple(KeyCode.Enter)); // expand /src

        view.SetSnapshot(SampleSnapshot()); // fresh node objects, same keys

        await Assert.That(view.VisibleCount).IsEqualTo(4);
        await Assert.That(view.IsExpanded(view.Roots[0])).IsTrue();
    }

    [Test]
    public async Task KeyGate_RefusesModifiedRunesButConsumesPlainOnes()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());

        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('j'), KeyModifiers.Shift))).IsFalse();
        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('j'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(view.HandleKey(KeyEvent.Char(new Rune('j')))).IsTrue();
        await Assert.That(view.CursorIndex).IsEqualTo(1);
    }

    [Test]
    public async Task Preview_CallbackReceivesSelectedNodeAndPaintsLines()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());
        GenericTreeNode? seen = null;
        view.Preview = node =>
        {
            seen = node;
            return new[] { $"path={node.Key}", "preview-line" };
        };

        await Assert.That(view.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue(); // README.md
        var buffer = TestRender.Buffer(60, 8);
        view.Paint(buffer, new Rect(0, 0, 60, 8));

        await Assert.That(seen?.Key).IsEqualTo("/README.md");
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("path=/README.md")).IsTrue();
        await Assert.That(art.Contains("preview-line")).IsTrue();
    }

    [Test]
    public async Task Preview_NoCallback_ShowsPlaceholder()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());
        var buffer = TestRender.Buffer(60, 8);

        view.Paint(buffer, new Rect(0, 0, 60, 8));

        await Assert.That(ScreenFixtures.Art(buffer).Contains("(no preview)")).IsTrue();
    }

    [Test]
    public async Task Paint_SplitsTreeAndPreviewIntoTwoPanes()
    {
        var view = GenericTreeView.FromSnapshot(SampleSnapshot());
        view.Preview = node => new[] { $"detail:{node.Label}" };
        var buffer = TestRender.Buffer(60, 8);

        view.Paint(buffer, new Rect(0, 0, 60, 8));
        string art = ScreenFixtures.Art(buffer);

        await Assert.That(art.Contains("src")).IsTrue();
        await Assert.That(art.Contains("README.md")).IsTrue();
        await Assert.That(art.Contains("Preview: src")).IsTrue();
        await Assert.That(art.Contains("detail:src")).IsTrue();
    }
}
