using Harbor.TestKit;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1224 (TR5): priority-ordered <see cref="IPanelWidgetDecoder" /> list
///     extends the three legacy <c>WidgetToRows</c> shapes without breaking them.
/// </summary>
public class PanelWidgetDecoderTests
{
    private sealed record ProbeWidget(string Value);

    private sealed class ProbeDecoder(int priority, string row) : IPanelWidgetDecoder
    {
        public int Priority => priority;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is ProbeWidget)
            {
                rows = [row];
                return true;
            }

            rows = null;
            return false;
        }
    }

    private static string Joined(object? widget) =>
        string.Join("\n", CellForgePanelAdapter.RenderToRows(
            new TestPanelProvider("stub-decoder", "Stub Decoder", widget), new PanelContext(new UiState(), 80, 24)));

    [Test]
    public async Task LegacyForms_Still_Pass_Through()
    {
        await Assert.That(Joined(null)).IsEqualTo(string.Empty);
        await Assert.That(Joined("a\nb")).IsEqualTo("a\nb");
        await Assert.That(Joined((IReadOnlyList<string>)["x"])).IsEqualTo("x");
        await Assert.That(Joined((IEnumerable<string>)["y"])).IsEqualTo("y");
        await Assert.That(Joined(42)).IsEqualTo("42");
    }

    [Test]
    public async Task Richer_Widgets_Decode_To_Rows()
    {
        await Assert.That(Joined(new PanelProgress("build", 0.5))).IsEqualTo("build [########--------] 50%");
        await Assert.That(Joined(new PanelTable(["a", "b"], [["1", "2"]]))).IsEqualTo("a | b\n-----\n1 | 2");
        await Assert.That(Joined(new PanelTree([new PanelTreeBranch("root", [new PanelTreeLeaf("leaf")])])))
            .IsEqualTo("root\n  leaf");
        await Assert.That(Joined(new PanelForm("opts", [("k", "v")]))).IsEqualTo("opts\nk: v");
    }

    /// <summary>
    ///     #1521: the tree is a part-whole structure, so a parent owns its
    ///     children. The rows are the ones the flat depth list produced: one
    ///     row per node, two spaces of indent per level.
    /// </summary>
    [Test]
    public async Task Tree_Nesting_Zero_One_And_Many()
    {
        // 0 children.
        await Assert.That(Joined(new PanelTree([new PanelTreeLeaf("solo")]))).IsEqualTo("solo");
        await Assert.That(Joined(new PanelTree([new PanelTreeBranch("solo")]))).IsEqualTo("solo");

        // 1 child.
        await Assert.That(Joined(new PanelTree([new PanelTreeBranch("root", [new PanelTreeLeaf("child")])])))
            .IsEqualTo("root\n  child");

        // N children, two levels deep: a parent, then its own children.
        await Assert.That(Joined(new PanelTree(
        [
            new PanelTreeBranch(
                "root",
                [
                    new PanelTreeBranch("a", [new PanelTreeLeaf("a1"), new PanelTreeLeaf("a2")]),
                    new PanelTreeLeaf("b"),
                ]),
        ]))).IsEqualTo("root\n  a\n    a1\n    a2\n  b");
    }

    /// <summary>
    ///     #1521: an empty tree is empty rows, not a missing widget. The
    ///     decoder claims it, the same way the flat depth list did.
    /// </summary>
    [Test]
    public async Task Tree_Empty_Renders_No_Rows()
    {
        await Assert.That(Joined(new PanelTree())).IsEqualTo(string.Empty);
        await Assert.That(Joined(new PanelTree([]))).IsEqualTo(string.Empty);
    }

    /// <summary>
    ///     #1521: the tree decoder claims the tree shape and nothing else.
    ///     An unknown widget falls through to the legacy shapes.
    /// </summary>
    [Test]
    public async Task Tree_Decoder_Declines_A_Non_Tree_Widget()
    {
        var decoder = new PanelWidgetDecoders.Tree();

        bool claimed = decoder.TryDecode(
            new PanelForm("opts", [("k", "v")]),
            new PanelContext(new UiState(), 80, 24),
            out IReadOnlyList<string>? rows);

        await Assert.That(claimed).IsFalse();
        await Assert.That(rows).IsNull();
    }

    /// <summary>
    ///     #1521: siblings keep their order, and a subtree mounted twice is
    ///     rendered twice. The walk follows the structure, it does not
    ///     de-duplicate it.
    /// </summary>
    [Test]
    public async Task Tree_Keeps_Sibling_Order()
    {
        IPanelTreeNode shared = new PanelTreeLeaf("shared");

        await Assert.That(Joined(new PanelTree(
        [
            new PanelTreeBranch("root", [shared, shared]),
            new PanelTreeLeaf("last"),
        ]))).IsEqualTo("root\n  shared\n  shared\nlast");
    }

    [Test]
    public async Task Higher_Priority_Decoder_Wins()
    {
        try
        {
            CellForgePanelAdapter.RegisterDecoder(new ProbeDecoder(10, "low"));
            CellForgePanelAdapter.RegisterDecoder(new ProbeDecoder(200, "high"));
            await Assert.That(Joined(new ProbeWidget("v"))).IsEqualTo("high");
        }
        finally
        {
            CellForgePanelAdapter.ResetDecoders();
        }

        await Assert.That(Joined("plain")).IsEqualTo("plain");
    }
}
