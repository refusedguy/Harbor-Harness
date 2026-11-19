using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #3629: the CLI palette drives the same query-history, usage-ranking
/// and alias policies as the desktop view-model. These tests go through the
/// real <see cref="CommandPaletteView"/> key path, not a policy double.
/// </summary>
public class CommandPalettePoliciesTests
{
    private static readonly CommandItem[] Commands =
    [
        new("beta", "Beta", "second candidate"),
        new("bravo", "Bravo", "first candidate"),
        new("exit", "Exit", "leave harbor"),
        new("help", "Help", "list commands"),
    ];

    private static KeyEvent Key(KeyCode code) => KeyEvent.Simple(code);

    private static void Type(CommandPaletteView palette, string text)
    {
        foreach (char c in text)
        {
            _ = palette.HandleKey(KeyEvent.Char(new Rune(c)));
        }
    }

    [Test]
    public async Task InvokedRow_LeadsTheNextFilteredList()
    {
        var palette = new CommandPaletteView();
        string? committed = null;
        palette.OnCommit = item => committed = item.Id;

        palette.Show(Commands);
        Type(palette, "bravo");
        _ = palette.HandleKey(Key(KeyCode.Enter));

        await Assert.That(committed).IsEqualTo("bravo");

        palette.Show(Commands);
        Type(palette, "b");

        await Assert.That(palette.Results.Any(r => r.Id == "beta")).IsTrue();
        await Assert.That(palette.Results[0].Id).IsEqualTo("bravo");
    }

    [Test]
    public async Task UpDown_WalkRecentQueries_AndRestoreTypedPrefix()
    {
        var palette = new CommandPaletteView();
        palette.Show(Commands);
        Type(palette, "beta");
        _ = palette.HandleKey(Key(KeyCode.Enter));

        await Assert.That(palette.RecentQueries.Count).IsEqualTo(1);
        await Assert.That(palette.RecentQueries[0]).IsEqualTo("beta");

        palette.Show(Commands);
        Type(palette, "be");
        _ = palette.HandleKey(Key(KeyCode.Up));

        await Assert.That(palette.Query).IsEqualTo("beta");

        _ = palette.HandleKey(Key(KeyCode.Down));

        await Assert.That(palette.Query).IsEqualTo("be");
    }

    [Test]
    public async Task ManualTyping_EndsAnInFlightHistoryWalk()
    {
        var palette = new CommandPaletteView();
        palette.Show(Commands);
        Type(palette, "beta");
        _ = palette.HandleKey(Key(KeyCode.Enter));

        palette.Show(Commands);
        Type(palette, "be");
        _ = palette.HandleKey(Key(KeyCode.Up));
        Type(palette, "x");

        await Assert.That(palette.Query).IsEqualTo("betax");

        _ = palette.HandleKey(Key(KeyCode.Up));

        await Assert.That(palette.Query).IsEqualTo("betax");
    }

    [Test]
    public async Task Alias_FindsTheCanonicalRow()
    {
        var palette = new CommandPaletteView();
        palette.AddAlias("quit", "exit");
        palette.Show(Commands);

        Type(palette, "quit");

        await Assert.That(palette.Results.Count).IsEqualTo(1);
        await Assert.That(palette.Results[0].Id).IsEqualTo("exit");
    }

    [Test]
    public async Task AliasLookup_IgnoresCase()
    {
        var palette = new CommandPaletteView();
        palette.AddAlias("halp", "help");
        palette.Show(Commands);

        Type(palette, "HaLp");

        await Assert.That(palette.Results.Single().Id).IsEqualTo("help");
    }

    [Test]
    public async Task FailedInvocation_DoesNotPromoteTheRow()
    {
        var palette = new CommandPaletteView();
        palette.OnCommit = _ => throw new InvalidOperationException("boom");
        palette.Show(Commands);
        Type(palette, "bravo");

        try
        {
            _ = palette.HandleKey(Key(KeyCode.Enter));
        }
        catch (InvalidOperationException)
        {
            // The host handler threw; the palette must not record the usage.
        }

        palette.OnCommit = null;
        palette.Show(Commands);
        Type(palette, "b");

        await Assert.That(palette.Results[0].Id).IsNotEqualTo("bravo");
    }
}
