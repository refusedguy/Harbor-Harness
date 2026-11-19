// #3084: regression cover for the product view : State -> Tree envelope.
// The generator's BuildRoot is part of the CellForge compilation, so these
// tests drive the same generated body the live renderer drives. They assert
// the two things the envelope exists to guarantee: a value-only state change
// becomes a mutation batch (not a rebuild), and per-widget focus/scroll state
// survives that batch.

using System.Linq;
using Harbor.Tui.CellForge.Generic;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

public class StateTreeViewTests
{
    private static UiState State(string input) =>
        new UiState().SetInput(InputModel.Empty.SetText(input));

    [Test]
    public async Task BuildRoot_ProducesTheTemplateTree_AndFirstSyncBuildsIt()
    {
        var view = new StateTreeView();

        await Assert.That(view.Sync(State("alpha"))).IsTrue();
        await Assert.That(view.NodeCount).IsEqualTo(3);
        await Assert.That(view.LastWasIncremental).IsFalse();
        await Assert.That(view.RootId).IsGreaterThan(0);
        await Assert.That(view.LastMutationOps.Any(op => op == "CreateElement")).IsTrue();
    }

    [Test]
    public async Task ValueChange_AppliesOnlyTextMutations_OnTheRetainedTree()
    {
        var view = new StateTreeView();
        await Assert.That(view.Sync(State("alpha"))).IsTrue();
        int root = view.RootId;
        int revision = view.Revision;

        await Assert.That(view.Sync(State("beta"))).IsTrue();

        await Assert.That(view.LastWasIncremental).IsTrue();
        await Assert.That(view.RootId).IsEqualTo(root);
        await Assert.That(view.Revision).IsGreaterThan(revision);
        await Assert.That(view.LastMutationCount).IsEqualTo(1);
        await Assert.That(view.LastMutationOps.All(op => op == "SetText")).IsTrue();
    }

    [Test]
    public async Task FocusAndScroll_SurviveAValueOnlyReconciliation()
    {
        var view = new StateTreeView();
        await Assert.That(view.Sync(State("one"))).IsTrue();
        var buffer = new ScreenBuffer(40, 8);
        view.Render(40, 8, buffer);

        var state = view.ElementStateAtLine(1);
        state.Focused = true;
        state.ScrollOffset = 7;

        await Assert.That(view.Sync(State("two"))).IsTrue();
        view.Render(40, 8, buffer);

        var after = view.ElementStateAtLine(1);
        await Assert.That(after).IsSameReferenceAs(state);
        await Assert.That(after.Focused).IsTrue();
        await Assert.That(after.ScrollOffset).IsEqualTo(7);
        await Assert.That(view.LastWasIncremental).IsTrue();
    }
}
