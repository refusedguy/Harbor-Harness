// CommandPaletteAliasTests.cs — issue #2682, item Z03.
//
// The Avalonia palette matched a query only against a row's label and hint.
// A documented slash synonym such as "quit" (/exit) or "m" (/model) is in
// neither, so typing it found nothing even though the CLI accepts it. Z03
// asks for an exact-query alias layer: rewrite the query before the normal
// filter runs. These tests pin that behaviour through the shared palette
// view-model, with no Avalonia UI loop involved.

using Harbor.Desktop.ViewModels;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class CommandPaletteAliasTests
{
    private sealed class TestPalette : CommandPaletteViewModelBase
    {
        public TestPalette()
            : base(new TestDispatcherAdapter(), NullLogger.Instance)
        {
        }

        public void Add(string label, string hint) =>
            AllCommands.Add(new CommandResultViewModel("slash", label, hint, () => { }));

        public void Alias(string alias, string canonical) => CommandAliases[alias] = canonical;

        public void Filter(string query) => Refilter(query);
    }

    [Test]
    public async Task AliasedQuery_FindsTheCanonicalRow()
    {
        var palette = new TestPalette();
        palette.Add("/exit", "Exit Harbor");
        palette.Add("/new", "Start a new session");
        palette.Add("/model", "Switch the active LLM model");
        palette.Alias("quit", "/exit");

        palette.Filter("quit");

        await Assert.That(palette.Results.Count).IsEqualTo(1);
        await Assert.That(palette.Results[0].Label).IsEqualTo("/exit");
        await Assert.That(palette.Results[0].Hint).IsEqualTo("Exit Harbor");
    }

    [Test]
    public async Task AliasLookup_IsCaseInsensitive()
    {
        var vm = new TestPalette();
        vm.Add("/exit", "Exit Harbor");
        vm.Alias("quit", "/exit");

        vm.Filter("QuIt");

        await Assert.That(vm.Results.Single().Label).IsEqualTo("/exit");
    }

    [Test]
    public async Task Alias_RewritesToASubstringQuery_NotOnlyAnExactLabel()
    {
        var vm = new TestPalette();
        vm.Add("Stop agent", "AgentStop");
        vm.Add("Refresh session list", "SessionRefresh");
        vm.Alias("halt", "agent");

        vm.Filter("halt");

        await Assert.That(vm.Results.Count).IsEqualTo(1);
        await Assert.That(vm.Results[0].Label).IsEqualTo("Stop agent");
    }

    [Test]
    public async Task NonAliasedQuery_StillFiltersNormally()
    {
        var vm = new TestPalette();
        vm.Add("/exit", "Exit Harbor");
        vm.Add("/new", "Start a new session");
        vm.Alias("quit", "/exit");

        vm.Filter("new");
        await Assert.That(vm.Results.Single().Label).IsEqualTo("/new");

        vm.Filter("exit");
        await Assert.That(vm.Results.Single().Label).IsEqualTo("/exit");
    }

    [Test]
    public async Task AliasToAMissingTarget_ReturnsNoRows()
    {
        var vm = new TestPalette();
        vm.Add("/exit", "Exit Harbor");
        vm.Alias("ghost", "/nothing");

        vm.Filter("ghost");

        await Assert.That(vm.Results).IsEmpty();
    }

    [Test]
    public async Task EmptyQuery_IsNotRewritten()
    {
        var vm = new TestPalette();
        vm.Add("/exit", "Exit Harbor");
        vm.Add("/new", "Start a new session");
        vm.Alias(string.Empty, "/nothing");

        vm.Filter(string.Empty);

        await Assert.That(vm.Results.Count).IsEqualTo(2);
    }
}
