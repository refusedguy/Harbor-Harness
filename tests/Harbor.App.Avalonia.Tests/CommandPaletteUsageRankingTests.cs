// CommandPaletteUsageRankingTests.cs — issue #2682, item Z02.
//
// The Avalonia command palette used to order its rows by fuzzy score alone,
// so a command the user ran every day sat next to one they had never seen.
// Z02 asks for usage to be considered before the fuzzy sort. These tests
// pin the observable behaviour of that ranking through the shared palette
// view-model, with no Avalonia UI loop involved.

using Harbor.Desktop.ViewModels;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class CommandPaletteUsageRankingTests
{
    private sealed class TestPalette : CommandPaletteViewModelBase
    {
        public TestPalette()
            : base(new TestDispatcherAdapter(), NullLogger.Instance)
        {
        }

        public void Add(string label) => Add(label, () => { });

        public void Add(string label, Action action) =>
            AllCommands.Add(new CommandResultViewModel("command", label, label + "-key", action));

        public void Filter(string query) => Refilter(query);

        public void InvokeFirst() => InvokeSelected();
    }

    [Test]
    public async Task InvokedCommand_LeadsTheUnfilteredList()
    {
        var vm = new TestPalette();
        vm.Add("Alpha");
        vm.Add("Beta");
        vm.Add("Gamma");
        vm.Filter(string.Empty);

        await Assert.That(vm.Results[0].Label).IsEqualTo("Alpha");
        await Assert.That(vm.Results[1].Label).IsEqualTo("Beta");
        await Assert.That(vm.Results[2].Label).IsEqualTo("Gamma");

        // Invoke through a narrowed query, then widen back to the full list.
        vm.Filter("beta");
        await Assert.That(vm.Results.Count).IsEqualTo(1);
        vm.InvokeFirst();

        vm.Filter(string.Empty);
        await Assert.That(vm.Results[0].Label).IsEqualTo("Beta");
        await Assert.That(vm.Results[1].Label).IsEqualTo("Alpha");
        await Assert.That(vm.Results[2].Label).IsEqualTo("Gamma");
    }

    [Test]
    public async Task UsageRanking_IsAppliedBeforeFuzzyScore()
    {
        var vm = new TestPalette();
        vm.Add("Zebra");
        vm.Add("Zeb");
        vm.Filter("zeb");

        // "zeb" is the shorter label, so it wins the fuzzy tie-break first.
        await Assert.That(vm.Results[0].Label).IsEqualTo("Zeb");

        // Promote the weaker fuzzy match, then re-run the original query.
        vm.Filter("zebra");
        vm.InvokeFirst();
        vm.Filter("zeb");

        await Assert.That(vm.Results[0].Label).IsEqualTo("Zebra");
        await Assert.That(vm.Results[1].Label).IsEqualTo("Zeb");
    }

    [Test]
    public async Task FailedInvocation_DoesNotPromoteTheCommand()
    {
        var vm = new TestPalette();
        vm.Add("Alpha");
        vm.Add("Beta", () => throw new InvalidOperationException("boom"));

        vm.Filter("beta");
        vm.InvokeFirst();

        vm.Filter(string.Empty);
        await Assert.That(vm.Results[0].Label).IsEqualTo("Alpha");
        await Assert.That(vm.Results[1].Label).IsEqualTo("Beta");
    }
}
