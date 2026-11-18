// CommandPaletteWeightedFuzzyTests.cs — issue #2681, item G85.
//
// The palette filter looks at Label and Hint, but ranking used to score the
// Label alone. A row whose query hit only its Hint was therefore sorted as a
// non-match. These tests pin the weighted ranking through the shared palette
// view-model, with no Avalonia UI loop involved.

using Harbor.Desktop.ViewModels;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class CommandPaletteWeightedFuzzyTests
{
    private sealed class TestPalette : CommandPaletteViewModelBase
    {
        public TestPalette()
            : base(new TestDispatcherAdapter(), NullLogger.Instance)
        {
        }

        public void Add(string label, string hint, string kind = "command") =>
            AllCommands.Add(new CommandResultViewModel(kind, label, hint, () => { }));

        public void Filter(string query) => Refilter(query);
    }

    [Test]
    public async Task WeightedScore_RanksFieldsByWeight()
    {
        const string longLabel = "Open the file tree viewer with a very long and descriptive label text";

        // Both rows match "tree", but only the first one matches in its label.
        // The label is long, so its raw score is low; the second row's short
        // hint scores higher even after the 0.5 secondary-field weight.
        var hintWins = new TestPalette();
        hintWins.Add(longLabel, "browser");
        hintWins.Add("Files", "tree");
        hintWins.Filter("tree");
        await Assert.That(hintWins.Results[0].Label).IsEqualTo("Files");

        // Same query length in both fields. The primary-field weight (1.0)
        // keeps a label hit ahead of a hint hit (0.5).
        var labelWins = new TestPalette();
        labelWins.Add("tree", "browser");
        labelWins.Add("Files", "tree-viewer");
        labelWins.Filter("tree");
        await Assert.That(labelWins.Results[0].Label).IsEqualTo("tree");
    }

    [Test]
    public async Task CategoryWeight_BreaksTieWhenBothOtherFieldsAreEqual()
    {
        var vm = new TestPalette();
        // The two "Switch view" rows differ only in category. Their hint is
        // deliberately longer than 100 characters, so its substring score
        // goes non-positive and the 0.3 category weight decides the order.
        string longHint = "session " + new string('x', 110);
        vm.Add("Switch view", longHint, "session");
        vm.Add("Switch view", longHint, "command");
        // A short hint still outranks a category-only hit (0.5 > 0.3).
        vm.Add("Files", "sessions");
        vm.Filter("session");

        await Assert.That(vm.Results.Count).IsEqualTo(3);
        await Assert.That(vm.Results[0].Label).IsEqualTo("Files");
        await Assert.That(vm.Results[1].Kind).IsEqualTo("session");
        await Assert.That(vm.Results[2].Kind).IsEqualTo("command");
    }
}
