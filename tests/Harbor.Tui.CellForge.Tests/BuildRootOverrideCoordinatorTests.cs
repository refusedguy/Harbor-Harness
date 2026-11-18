using System;
using System.Collections.Generic;
using System.Linq;
using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen.DevReload;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2435: installing a BuildRoot override swaps only the tree body.
///     The coordinator records the explicit reset list, separates rerender
///     from recreate on parameter changes, and proves the host resources are
///     the same objects after the swap.
/// </summary>
/// <remarks>
///     The override slot is process-wide, so this class is serialized with a
///     NotInParallel key. Every test restores the slot it installs.
/// </remarks>
[NotInParallel("root-builder-override")]
public class BuildRootOverrideCoordinatorTests
{
    private const string Markup = """
        panel "main":
            .text "hello"
            .height 10
            text "done"
        """;

    private static DevReloadInterpreter Interpreter()
    {
        if (!DevReloadInterpreter.TryCreate(Markup, out var interpreter, out string error))
            throw new InvalidOperationException("Fixture markup must parse: " + error);
        return interpreter!;
    }

    private static Action<Action<int>, Action<int, string>, Action> Sink() =>
        (_, _, _) => { };

    [Test]
    public async Task Decide_RemovedParameter_Recreates()
    {
        var transition = BuildRootOverrideCoordinator.Decide(["title", "height"], ["title"]);

        await Assert.That(transition).IsEqualTo(OverrideTransition.Recreate);
        await Assert.That(OverrideResetEffects.For(transition)).IsEqualTo(OverrideResetEffects.Recreate);
        await Assert.That(OverrideResetEffects.Recreate.ResetWidgetState).IsTrue();
        await Assert.That(OverrideResetEffects.Recreate.ResetFocus).IsTrue();
        await Assert.That(OverrideResetEffects.Recreate.ResetHandlers).IsTrue();
    }

    [Test]
    public async Task Decide_AddedOrChangedParameter_Rerenders()
    {
        var added = BuildRootOverrideCoordinator.Decide(["title"], ["title", "height"]);

        await Assert.That(added).IsEqualTo(OverrideTransition.Rerender);
        await Assert.That(OverrideResetEffects.For(added)).IsEqualTo(OverrideResetEffects.Rerender);
        await Assert.That(OverrideResetEffects.Rerender.ResetWidgetState).IsFalse();
        await Assert.That(OverrideResetEffects.Rerender.ResetFocus).IsFalse();
        await Assert.That(OverrideResetEffects.Rerender.ResetHandlers).IsFalse();
    }

    [Test]
    public async Task CanOverride_RejectsParseFailureAndLogicRestart()
    {
        await Assert.That(BuildRootOverrideCoordinator.CanOverride(ReloadOutcome.Unchanged)).IsTrue();
        await Assert.That(BuildRootOverrideCoordinator.CanOverride(ReloadOutcome.Patched)).IsTrue();
        await Assert.That(BuildRootOverrideCoordinator.CanOverride(ReloadOutcome.Rebuilt)).IsTrue();
        await Assert.That(BuildRootOverrideCoordinator.CanOverride(ReloadOutcome.Rejected)).IsFalse();
        await Assert.That(BuildRootOverrideCoordinator.CanOverride(ReloadOutcome.RestartRequired)).IsFalse();
    }

    [Test]
    public async Task Install_SwapsDelegate_AndReceiptKeepsEveryHostResource()
    {
        object host = new();
        object loop = new();
        object probe = new();
        object mode = new();
        var coordinator = new BuildRootOverrideCoordinator(host, loop, probe, mode);
        var builder = Sink();
        RootBuilderOverride.Clear();

        try
        {
            var receipt = coordinator.Install(
                builder, ReloadOutcome.Patched, ["title", "height"], ["title"], "sha256:new");

            await Assert.That(RootBuilderOverride.Current).IsSameReferenceAs(builder);
            await Assert.That(receipt.Transition).IsEqualTo(OverrideTransition.Recreate);
            await Assert.That(receipt.Signature).IsEqualTo("sha256:new");
            await Assert.That(receipt.Generation).IsEqualTo(1);
            await Assert.That(coordinator.IsInstalled).IsTrue();

            await Assert.That(receipt.Host.Host).IsSameReferenceAs(host);
            await Assert.That(receipt.Host.EventLoop).IsSameReferenceAs(loop);
            await Assert.That(receipt.Host.CapabilityProbe).IsSameReferenceAs(probe);
            await Assert.That(receipt.Host.TerminalMode).IsSameReferenceAs(mode);
            await Assert.That(coordinator.Host).IsSameReferenceAs(receipt.Host);
        }
        finally
        {
            coordinator.Restore();
            RootBuilderOverride.Clear();
        }

        await Assert.That(RootBuilderOverride.Current).IsNull();
        await Assert.That(coordinator.IsInstalled).IsFalse();
    }

    [Test]
    public async Task Install_Restore_PutsPreviousBuilderBack()
    {
        var first = Sink();
        var second = Sink();
        RootBuilderOverride.Clear();
        var coordinator = new BuildRootOverrideCoordinator(new object(), new object(), new object(), new object());

        try
        {
            RootBuilderOverride.Install(first);
            var receipt = coordinator.Install(second, ReloadOutcome.Unchanged, ["title"], ["title"], "sig");
            await Assert.That(receipt.Transition).IsEqualTo(OverrideTransition.Rerender);
            await Assert.That(RootBuilderOverride.Current).IsSameReferenceAs(second);

            coordinator.Restore();
            await Assert.That(RootBuilderOverride.Current).IsSameReferenceAs(first);
            await Assert.That(coordinator.Generation).IsEqualTo(1);
            coordinator.Restore(); // idempotent: must not clear somebody else's slot
            await Assert.That(RootBuilderOverride.Current).IsSameReferenceAs(first);
        }
        finally
        {
            RootBuilderOverride.Clear();
        }
    }

    [Test]
    public async Task FromInterpreter_ReplaysDensePreOrderSequences()
    {
        var replay = BuildRootOverrideCoordinator.FromInterpreter(Interpreter());
        var begins = new List<int>();
        var texts = new List<(int, string)>();
        int ends = 0;

        replay(begins.Add, (sequence, value) => texts.Add((sequence, value)), () => ends++);

        await Assert.That(begins.SequenceEqual([0, 2])).IsTrue();
        await Assert.That(texts.Count).IsEqualTo(2);
        await Assert.That(texts[0]).IsEqualTo((1, "hello"));
        await Assert.That(texts[1]).IsEqualTo((3, "done"));
        await Assert.That(ends).IsEqualTo(2);
    }

    [Test]
    public async Task Install_RejectsOutcomeThatNeedsARestart()
    {
        var coordinator = new BuildRootOverrideCoordinator(new object(), new object(), new object(), new object());
        RootBuilderOverride.Clear();

        await Assert.ThrowsAsync<ArgumentException>(async () => await Task.Run(() =>
            coordinator.Install(Sink(), ReloadOutcome.RestartRequired, [], [], "sig")));

        await Assert.That(RootBuilderOverride.Current).IsNull();
        await Assert.That(coordinator.IsInstalled).IsFalse();
    }
}
