// CommandPaletteQueryHistoryTests.cs — issue #2682, item Z01 (plus the Z10
// prefix-reset quality rule). The palette remembers typed queries and lets
// Up/Down walk only those that start with the current prefix. These tests pin
// the pure QueryHistory behaviour and its wiring through the shared palette
// view-model, without an Avalonia event loop.

using Harbor.Desktop.ViewModels;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class CommandPaletteQueryHistoryTests
{
    private sealed class TestPalette : CommandPaletteViewModelBase
    {
        public TestPalette()
            : base(new TestDispatcherAdapter(), NullLogger.Instance)
        {
        }

        public void Add(string label) =>
            AllCommands.Add(new CommandResultViewModel("command", label, label, () => { }));

        public void InvokeFirst() => InvokeSelected();
    }

    [Test]
    public async Task Add_KeepsMostRecentLast_AndDropsDuplicates()
    {
        var history = new QueryHistory();
        history.Add("alpha");
        history.Add("amber");
        history.Add("alpha");
        history.Add("   ");

        await Assert.That(history.Entries.Count).IsEqualTo(2);
        await Assert.That(history.Entries[0]).IsEqualTo("amber");
        await Assert.That(history.Entries[1]).IsEqualTo("alpha");
    }

    [Test]
    public async Task MoveBack_WalksOnlyPrefixMatches_NewestFirst()
    {
        var history = new QueryHistory();
        history.Add("alpha");
        history.Add("amber");
        history.Add("beta");
        history.Add("alpine");

        // "al" matches alp* and alph*; most recent match is "alpine".
        await Assert.That(history.MoveBack("al")).IsEqualTo("alpine");
        await Assert.That(history.MoveBack("alpine")).IsEqualTo("alpha");
        // At the oldest match the walk clamps instead of leaving history.
        await Assert.That(history.MoveBack("alpha")).IsEqualTo("alpha");
    }

    [Test]
    public async Task MoveBack_EmptyQuery_DoesNotNavigate()
    {
        var history = new QueryHistory();
        history.Add("alpha");

        await Assert.That(history.MoveBack(string.Empty)).IsNull();
        await Assert.That(history.IsNavigating).IsFalse();
    }

    [Test]
    public async Task PrefixMatching_IsCaseInsensitive()
    {
        var history = new QueryHistory();
        history.Add("Zebra");

        await Assert.That(history.MoveBack("zeb")).IsEqualTo("Zebra");
    }

    [Test]
    public async Task MoveForward_RestoresTypedQuery_ThenEndsNavigation()
    {
        var history = new QueryHistory();
        history.Add("alpha");
        history.Add("alpine");

        await Assert.That(history.MoveBack("al")).IsEqualTo("alpine");
        await Assert.That(history.MoveForward("alpine")).IsEqualTo("al");
        await Assert.That(history.IsNavigating).IsFalse();
        await Assert.That(history.MoveForward("al")).IsNull();
    }

    [Test]
    public async Task MoveForward_WalksTowardNewerMatches()
    {
        var history = new QueryHistory();
        history.Add("alpha");
        history.Add("amber");
        history.Add("alpine");

        await Assert.That(history.MoveBack("al")).IsEqualTo("alpine");
        await Assert.That(history.MoveBack("alpine")).IsEqualTo("alpha");
        await Assert.That(history.MoveForward("alpha")).IsEqualTo("alpine");
    }

    [Test]
    public async Task ResetNavigation_DropsInFlightWalk_AfterManualEdit()
    {
        var history = new QueryHistory();
        history.Add("alpha");
        history.Add("alpine");

        await Assert.That(history.MoveBack("al")).IsEqualTo("alpine");
        // Z10: the user typed over the box; the old prefix must not win.
        history.ResetNavigation();
        await Assert.That(history.IsNavigating).IsFalse();
        await Assert.That(history.MoveBack("b")).IsNull();
    }

    [Test]
    public async Task ViewModel_RecordsQueryOnInvoke_AndNavigatesIt()
    {
        var vm = new TestPalette();
        vm.Add("alpha");

        vm.Query = "alpha";
        vm.InvokeFirst();

        await Assert.That(vm.RecentQueries.Count).IsEqualTo(1);
        await Assert.That(vm.RecentQueries[0]).IsEqualTo("alpha");

        vm.Query = "al";
        await Assert.That(vm.TryNavigateHistory(up: true)).IsTrue();
        await Assert.That(vm.Query).IsEqualTo("alpha");
    }

    [Test]
    public async Task ViewModel_ManualQueryEdit_ResetsHistoryNavigation()
    {
        var vm = new TestPalette();
        vm.Add("alpha");

        vm.Query = "alpha";
        vm.InvokeFirst();

        vm.Query = "al";
        await Assert.That(vm.TryNavigateHistory(up: true)).IsTrue();
        await Assert.That(vm.Query).IsEqualTo("alpha");

        // Assigning a fresh query is a manual edit; the next Up starts over.
        vm.Query = "zz";
        await Assert.That(vm.TryNavigateHistory(up: true)).IsFalse();
        await Assert.That(vm.Query).IsEqualTo("zz");
    }
}
