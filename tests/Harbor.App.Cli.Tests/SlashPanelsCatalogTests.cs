using Harbor.App.Cli.Repl.Commands;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Slash-panels-first: the interactive catalog resolves
///     <c>/plugins</c>, <c>/providers</c> and <c>/tree</c> to palette
///     commands, so the consoleex REPL opens panels while the legacy
///     dispatcher keeps the textual non-interactive fallback.
/// </summary>
public class SlashPanelsCatalogTests
{
    [Test]
    public async Task Catalog_ResolvesTree_AsBranchTreePanel()
    {
        var catalog = ReplCommandCatalog.CreateDefault();

        bool found = catalog.TryResolve("tree", out var command);

        await Assert.That(found).IsTrue();
        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Id).IsEqualTo("tree");
    }

    [Test]
    public async Task Catalog_ResolvesProviders_AsAuthStatusPanel()
    {
        var catalog = ReplCommandCatalog.CreateDefault();

        bool found = catalog.TryResolve("providers", out var command);

        await Assert.That(found).IsTrue();
        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Id).IsEqualTo("providers");
    }

    [Test]
    public async Task Catalog_ResolvesPlugins_AndAlias_AsTogglePanel()
    {
        var catalog = ReplCommandCatalog.CreateDefault();

        bool found = catalog.TryResolve("plugins", out var command);
        bool aliasFound = catalog.TryResolve("plugin", out var alias);

        await Assert.That(found).IsTrue();
        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Id).IsEqualTo("plugins");
        await Assert.That(aliasFound).IsTrue();
        await Assert.That(alias).IsNotNull();
        await Assert.That(alias!.Id).IsEqualTo("plugins");
    }

    [Test]
    public async Task Catalog_UnknownCommand_DoesNotResolve()
    {
        var catalog = ReplCommandCatalog.CreateDefault();

        await Assert.That(catalog.TryResolve("zzz-no-such-command", out _)).IsFalse();
    }
}
