// CommandNameHumanizerTests.cs — issue #2682, item Z09. The palette shows a
// technical key beside each built-in command; humanization turns it into a
// readable name without changing the raw value the filter searches.

using Harbor.Desktop.ViewModels;
using Harbor.Desktop.Abstractions.Commands;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class CommandNameHumanizerTests
{
    [Test]
    public async Task PascalCaseKey_SplitsIntoWords()
    {
        await Assert.That(CommandNameHumanizer.Humanize("CodeEditorView"))
            .IsEqualTo("Code Editor View");
        await Assert.That(CommandNameHumanizer.Humanize("ChatView"))
            .IsEqualTo("Chat View");
    }

    [Test]
    public async Task Separators_SplitIntoWords()
    {
        await Assert.That(CommandNameHumanizer.Humanize("editor::GoToDefinition"))
            .IsEqualTo("Editor Go To Definition");
        await Assert.That(CommandNameHumanizer.Humanize("session_new"))
            .IsEqualTo("Session New");
        await Assert.That(CommandNameHumanizer.Humanize("/model"))
            .IsEqualTo("Model");
    }

    [Test]
    public async Task AcronymBeforeCapitalizedWord_GetsItsOwnWord()
    {
        await Assert.That(CommandNameHumanizer.Humanize("HTTPServer"))
            .IsEqualTo("Http Server");
    }

    [Test]
    public async Task EmptyOrWhitespace_ReturnsEmpty()
    {
        await Assert.That(CommandNameHumanizer.Humanize(string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(CommandNameHumanizer.Humanize("   ")).IsEqualTo(string.Empty);
        await Assert.That(CommandNameHumanizer.Humanize(null)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Humanize_IsIdempotent()
    {
        foreach (string input in new[] { "ChatView", "editor::GoToDefinition", "HTTPServer", "session_new" })
        {
            string once = CommandNameHumanizer.Humanize(input);
            await Assert.That(CommandNameHumanizer.Humanize(once)).IsEqualTo(once);
        }
    }

    [Test]
    public async Task DisplayHint_HumanizesOnlyBuiltInCommandRows()
    {
        var command = new CommandResultViewModel("command", "Switch to code editor", "CodeEditorView", () => { });
        var slash = new CommandResultViewModel("slash", "/help", "Show help", () => { });

        await Assert.That(command.DisplayHint).IsEqualTo("Code Editor View");
        // The raw key is still on the row, so a search for "CodeEditorView"
        // keeps finding it (Refilter matches Hint, not DisplayHint).
        await Assert.That(command.Hint).IsEqualTo("CodeEditorView");
        await Assert.That(slash.DisplayHint).IsEqualTo("Show help");
    }
}
