using Harbor.Tui.CellForge.Generic;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2351: idempotent layout normalization and reconciliation by stable node
/// id. The spec half ("every node seated once, ratios canonical") lives in
/// <see cref="GenericLayoutPlan"/>; the tree half (ratio fixpoint) lives in
/// <see cref="LayoutTree.Normalize"/>; surviving panels keep their object
/// (and therefore scroll + focus) across <see cref="GenericScreen.Rebuild"/>.
/// </summary>
public sealed class GenericLayoutReconciliationTests
{
    private sealed class StubPanel(string id, int minW, int minH)
        : Panel(id, new Size(minW, minH), 0)
    {
        public override void Paint(ScreenBuffer buffer)
        {
        }
    }

    private static GenericNodeSpec Node(string id) => new() { Id = id, Title = id };

    [Test]
    public async Task Normalize_SeatsEveryNodeOnce_AndIsIdempotent()
    {
        var nodes = new[] { Node("a"), Node("b"), Node("c"), Node("d") };
        var splits = new[]
        {
            new GenericSplitSpec { TargetId = "a", NewPanelId = "b", Ratio = float.NaN },
            new GenericSplitSpec { TargetId = "a", NewPanelId = "b" }, // repeated seat
            new GenericSplitSpec { TargetId = "a", NewPanelId = "c", Ratio = 5f }, // clamped
        };

        var plan = GenericLayoutPlan.Normalize(nodes, splits);
        await Assert.That(plan.RootId).IsEqualTo("a");
        await Assert.That(plan.NodeIds).IsEquivalentTo(new[] { "a", "b", "c", "d" });
        await Assert.That(plan.Splits.Select(s => s.NewPanelId)).IsEquivalentTo(new[] { "b", "c", "d" });
        var ratios = plan.Splits.Select(s => s.Ratio).ToArray();
        await Assert.That(ratios.Length).IsEqualTo(3);
        await Assert.That(MathF.Abs(ratios[0] - 0.5f)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(ratios[1] - 0.95f)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(ratios[2] - 0.4f)).IsLessThan(1e-5f);

        var again = GenericLayoutPlan.Normalize(nodes, plan.ToSpecs());
        await Assert.That(again.RootId).IsEqualTo(plan.RootId);
        await Assert.That(again.NodeIds).IsEquivalentTo(plan.NodeIds);
        await Assert.That(again.Splits).IsEquivalentTo(plan.Splits);
    }

    [Test]
    public async Task Normalize_DefersSplitWithUnseatedTarget_ToDefaultSeat()
    {
        var nodes = new[] { Node("a"), Node("b"), Node("c") };
        var plan = GenericLayoutPlan.Normalize(
            nodes,
            [new GenericSplitSpec { TargetId = "c", NewPanelId = "b" }]);

        // "c" is declared but not seated when the split is read, so it is
        // dropped and both b and c are seated by the default pass from root.
        await Assert.That(plan.Splits.Select(s => s.NewPanelId)).IsEquivalentTo(new[] { "b", "c" });
        await Assert.That(plan.Splits.All(s => s.TargetId == "a")).IsTrue();
    }

    [Test]
    public async Task TreeNormalize_ClampsRatio_ToFixpoint()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        tree.Split("a", SplitDir.Horizontal, 5f, new StubPanel("b", 1, 1), gap: 0);
        tree.Solve(100, 10);
        int raw = tree["a"].Rect.Width;
        await Assert.That(raw).IsEqualTo(99);

        tree.Normalize();
        tree.Solve(100, 10);
        int normalized = tree["a"].Rect.Width;
        await Assert.That(normalized).IsEqualTo(95);

        tree.Normalize();
        tree.Solve(100, 10);
        await Assert.That(tree["a"].Rect.Width).IsEqualTo(normalized);
        await Assert.That(tree["b"].Rect.Width).IsEqualTo(5);
    }

    [Test]
    public async Task Rebuild_KeepsPanelInstance_Focus_AndScroll_ByStableId()
    {
        var list = new GenericSelectionList();
        list.SetItems(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"]);
        list.MoveTo(7);
        list.State.ScrollBy(3);
        int expectedSelected = list.SelectedIndex;
        int expectedOffset = list.Offset;

        var nodes = new[]
        {
            Node("head"),
            new GenericNodeSpec { Id = "list", Title = "list", Field = list },
        };
        var splits = new[] { new GenericSplitSpec { TargetId = "head", NewPanelId = "list" } };
        var screen = GenericScreen.Build(nodes, splits);
        screen.Solve(40, 8);
        screen.SetFocus("list");
        var oldPanel = screen.TryGetPanel("list");

        // A spec rebuild that recreates the field: reconciliation by id must
        // keep the OLD panel (and its scrolled field), not adopt the new one.
        var freshList = new GenericSelectionList();
        freshList.SetItems(["fresh"]);
        var freshNodes = new[]
        {
            Node("head"),
            new GenericNodeSpec { Id = "list", Title = "changed", Field = freshList },
        };
        var rebuilt = screen.Rebuild(freshNodes, splits);

        await Assert.That(ReferenceEquals(rebuilt.TryGetPanel("list"), oldPanel)).IsTrue();
        await Assert.That(ReferenceEquals(rebuilt.TryGetPanel("list")!.Field, list)).IsTrue();
        await Assert.That(rebuilt.FocusedId).IsEqualTo("list");
        await Assert.That(rebuilt.TryGetPanel("list")!.Focused).IsTrue();
        await Assert.That(list.SelectedIndex).IsEqualTo(expectedSelected);
        await Assert.That(list.Offset).IsEqualTo(expectedOffset);
    }

    [Test]
    public async Task Rebuild_AddsAndDropsPanels_AndFallsBackFocusToRoot()
    {
        var screen = GenericScreen.Build(
            [Node("a"), Node("b")],
            [new GenericSplitSpec { TargetId = "a", NewPanelId = "b" }]);
        screen.SetFocus("b");
        var rootPanel = screen.TryGetPanel("a");

        var rebuilt = screen.Rebuild(
            [Node("a"), Node("c")],
            [new GenericSplitSpec { TargetId = "a", NewPanelId = "c" }]);

        await Assert.That(rebuilt.Count).IsEqualTo(2);
        await Assert.That(rebuilt.TryGetPanel("b")).IsNull();
        await Assert.That(ReferenceEquals(rebuilt.TryGetPanel("a"), rootPanel)).IsTrue();
        await Assert.That(rebuilt.TryGetPanel("c")).IsNotNull();
        await Assert.That(rebuilt.FocusedId).IsEqualTo("a");
    }

    [Test]
    public async Task Rebuild_CarriesTerminalMode()
    {
        var screen = GenericScreen.Build([Node("a")]);
        screen.Complete(GenericScreenMode.Done);

        var rebuilt = screen.Rebuild([Node("a")]);

        await Assert.That(rebuilt.Mode).IsEqualTo(GenericScreenMode.Done);
    }
}
