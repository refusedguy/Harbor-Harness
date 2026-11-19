// #3087 (S6): proof that the BCL-only catalog has a real product consumer.
// The reference screen is the `harbor catalog` verb, not a test-only painter:
// these tests drive the same ConsoleAppFramework route a user calls.

using Harbor.App.Cli.Commands;
using Harbor.Tui.CellForge.Catalog;

namespace Harbor.App.Cli.Tests;

[NotInParallel]
public sealed class CatalogCommandTests : CafVerbTestBase
{
    [Test]
    public async Task Catalog_Verb_PaintsEveryDeclaredWidget()
    {
        var widgets = WidgetCatalog.Widgets;
        var (exit, output, error) = await RunCafAsync("catalog", []);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).Contains("CellForge widget catalog");
        await Assert.That(output).Contains("BCL-only / AOT-clean");
        foreach (var widget in widgets)
        {
            await Assert.That(output).Contains(widget.Name);
        }
    }

    [Test]
    public async Task Catalog_Help_ExitsZeroWithUsage() =>
        await AssertHelpExitsZeroWithUsage("catalog");
}
