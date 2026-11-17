using Harbor.Tui.CellForge.Palettes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Contract for the domain-free palette primitive (issue #2321): fuzzy search
/// over commands, alias table, and older/newer input history. No renderer, no
/// chat types, no session/worktree domain leaks.
/// </summary>
public class CommandPalettePrimitiveTests
{
    private static CommandPalettePrimitive Palette() => new(
    [
        new PaletteCommand("file.open", "File open", "open a file"),
        new PaletteCommand("git.status", "Git status", "show working tree", ["st", "gst"]),
        new PaletteCommand("git.branch", "Git branch", "switch branch", ["br"]),
    ]);

    [Test]
    public async Task EmptyQuery_ListsAllCommandsInSeedOrder()
    {
        var palette = Palette();

        await Assert.That(palette.Results.Count).IsEqualTo(3);
        await Assert.That(palette.Results[0].Command.Id).IsEqualTo("file.open");
        await Assert.That(palette.Results[1].Command.Id).IsEqualTo("git.status");
        await Assert.That(palette.Results[2].Command.Id).IsEqualTo("git.branch");
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task FuzzyQuery_DropsNonMatchesAndRanksExactSubstringFirst()
    {
        var palette = new CommandPalettePrimitive(
        [
            new PaletteCommand("a", "File open", "open a file"),
            new PaletteCommand("b", "Git status", "show working tree"),
            new PaletteCommand("c", "git", "short"),
        ]);

        palette.SetQuery("git");

        await Assert.That(palette.Results.Count).IsEqualTo(2);
        await Assert.That(palette.Results[0].Command.Id).IsEqualTo("c");
        await Assert.That(palette.Results[1].Command.Id).IsEqualTo("b");
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task FuzzyQuery_SearchesDetailAndId()
    {
        var palette = Palette();

        palette.SetQuery("working");

        await Assert.That(palette.Results.Count).IsEqualTo(1);
        await Assert.That(palette.Results[0].Command.Id).IsEqualTo("git.status");
    }

    [Test]
    public async Task DeclaredAlias_ExactMatch_OutranksTitleAndReportsAlias()
    {
        var palette = Palette();

        palette.SetQuery("gst");

        await Assert.That(palette.Results.Count).IsGreaterThan(0);
        await Assert.That(palette.Results[0].Command.Id).IsEqualTo("git.status");
        await Assert.That(palette.Results[0].MatchedAlias).IsEqualTo("gst");
    }

    [Test]
    public async Task DeclaredAlias_ResolvesTokenCaseInsensitively()
    {
        var palette = Palette();

        await Assert.That(palette.TryResolveAlias("BR", out var id)).IsTrue();
        await Assert.That(id).IsEqualTo("git.branch");
        await Assert.That(palette.TryResolveAlias("missing", out _)).IsFalse();
    }

    [Test]
    public async Task ExplicitAlias_JoinsSearchAndTable()
    {
        var palette = Palette();

        await Assert.That(palette.AddAlias("fo", "file.open")).IsTrue();
        await Assert.That(palette.TryResolveAlias("FO", out var id)).IsTrue();
        await Assert.That(id).IsEqualTo("file.open");

        palette.SetQuery("fo");
        await Assert.That(palette.Results.Count).IsEqualTo(1);
        await Assert.That(palette.Results[0].Command.Id).IsEqualTo("file.open");
        await Assert.That(palette.Results[0].MatchedAlias).IsEqualTo("fo");

        await Assert.That(palette.RemoveAlias("fo")).IsTrue();
        await Assert.That(palette.TryResolveAlias("fo", out _)).IsFalse();
        await Assert.That(palette.Results[0].MatchedAlias).IsNull();
    }

    [Test]
    public async Task AddAlias_RejectsBlankAndUnknownCommands()
    {
        var palette = Palette();

        await Assert.That(palette.AddAlias("  ", "file.open")).IsFalse();
        await Assert.That(palette.AddAlias("x", "not.a.command")).IsFalse();
        await Assert.That(palette.Aliases.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetCommands_ReindexesAliases()
    {
        var palette = Palette();
        await Assert.That(palette.TryResolveAlias("st", out _)).IsTrue();

        palette.SetCommands([new PaletteCommand("other", "Other", "no aliases")]);

        await Assert.That(palette.TryResolveAlias("st", out _)).IsFalse();
        await Assert.That(palette.Results.Count).IsEqualTo(1);
        await Assert.That(palette.Results[0].Command.Id).IsEqualTo("other");
    }

    [Test]
    public async Task Move_WrapsAtBothEndsAndIsNoOpWithoutResults()
    {
        var palette = Palette();

        palette.Move(-1);
        await Assert.That(palette.SelectedIndex).IsEqualTo(2);
        palette.Move(1);
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
        palette.Move(2);
        await Assert.That(palette.SelectedIndex).IsEqualTo(2);

        palette.SetQuery("no-such-command-xyz");
        await Assert.That(palette.Results.Count).IsEqualTo(0);
        await Assert.That(palette.SelectedIndex).IsEqualTo(-1);
        palette.Move(1);
        await Assert.That(palette.SelectedIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task Commit_RecordsCommandAndQuery()
    {
        var palette = Palette();
        palette.SetQuery("  st  ");

        var committed = palette.Commit();

        await Assert.That(committed).IsNotNull();
        await Assert.That(committed!.Id).IsEqualTo("git.status");
        await Assert.That(palette.HistoryCount).IsEqualTo(1);
        await Assert.That(palette.History[0]).IsEqualTo(new PaletteHistoryEntry("st", "git.status"));
    }

    [Test]
    public async Task Commit_WithoutSelection_ReturnsNull()
    {
        var palette = Palette();
        palette.SetQuery("nothing-matches-this");

        await Assert.That(palette.Commit()).IsNull();
        await Assert.That(palette.HistoryCount).IsEqualTo(0);
    }

    [Test]
    public async Task Record_CollapsesConsecutiveDuplicatesAndCapsCapacity()
    {
        var palette = new CommandPalettePrimitive(historyCapacity: 2);

        palette.Record("a", "one");
        palette.Record("a", "one");
        await Assert.That(palette.HistoryCount).IsEqualTo(1);

        palette.Record("b", "two");
        palette.Record("c", "three");

        await Assert.That(palette.HistoryCount).IsEqualTo(2);
        await Assert.That(palette.History[0]).IsEqualTo(new PaletteHistoryEntry("two", "b"));
        await Assert.That(palette.History[1]).IsEqualTo(new PaletteHistoryEntry("three", "c"));
    }

    [Test]
    public async Task History_OlderAndNewer_WalkEntriesAndAdoptQuery()
    {
        var palette = Palette();
        palette.Record("a", "first");
        palette.Record("b", "second");
        palette.Record("c", "third");

        await Assert.That(palette.HistoryIndex).IsEqualTo(3);
        await Assert.That(palette.CanGoOlder).IsTrue();
        await Assert.That(palette.CanGoNewer).IsFalse();

        await Assert.That(palette.HistoryOlder()).IsEqualTo("third");
        await Assert.That(palette.Query).IsEqualTo("third");
        await Assert.That(palette.HistoryOlder()).IsEqualTo("second");
        await Assert.That(palette.HistoryIndex).IsEqualTo(1);

        await Assert.That(palette.HistoryNewer()).IsEqualTo("third");
        await Assert.That(palette.HistoryIndex).IsEqualTo(2);
        await Assert.That(palette.HistoryNewer()).IsEqualTo(string.Empty);
        await Assert.That(palette.Query).IsEqualTo(string.Empty);
        await Assert.That(palette.HistoryIndex).IsEqualTo(3);
        await Assert.That(palette.CanGoNewer).IsFalse();
        await Assert.That(palette.HistoryNewer()).IsNull();

        await Assert.That(palette.HistoryOlder()).IsEqualTo("third");
        await Assert.That(palette.HistoryOlder()).IsEqualTo("second");
        await Assert.That(palette.HistoryOlder()).IsEqualTo("first");
        await Assert.That(palette.CanGoOlder).IsFalse();
        await Assert.That(palette.HistoryOlder()).IsNull();
    }

    [Test]
    public async Task ClearHistory_ResetsCursor()
    {
        var palette = Palette();
        palette.Record("a", "one");
        palette.Record("b", "two");
        palette.HistoryOlder();

        palette.ClearHistory();

        await Assert.That(palette.HistoryCount).IsEqualTo(0);
        await Assert.That(palette.HistoryIndex).IsEqualTo(0);
        await Assert.That(palette.CanGoOlder).IsFalse();
        await Assert.That(palette.CanGoNewer).IsFalse();
    }
}
