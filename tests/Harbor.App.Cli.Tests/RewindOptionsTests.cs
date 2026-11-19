using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2659 CLI surface: the rewind mode flags of <c>harbor sessions rewind</c>
///     are parsed as a pure helper. These tests pin the interface only; the
///     restore engine and write-tool wiring live in their own slices.
/// </summary>
public class RewindOptionsTests
{
    [Test]
    public async Task Parse_WithoutFiles_DefaultsToMessageRewind()
    {
        var result = RewindOptions.Parse(["s1", "cp1"]);

        await Assert.That(result.IsSuccess).IsTrue();
        if (result.IsFailure)
            return;
        var options = result.Value;
        await Assert.That(options.Mode).IsEqualTo(RewindMode.Messages);
        await Assert.That(options.IsFiles).IsFalse();
        await Assert.That(options.IsMessages).IsTrue();
        await Assert.That(options.IsBoth).IsFalse();
        await Assert.That(options.SessionId).IsEqualTo("s1");
        await Assert.That(options.CheckpointId).IsEqualTo("cp1");
        await Assert.That(options.ShowHelp).IsFalse();
    }

    [Test]
    public async Task Parse_FilesFlagBeforePositionals_SelectsFilesMode()
    {
        var result = RewindOptions.Parse(["--files", "s1", "cp1"]);

        await Assert.That(result.IsSuccess).IsTrue();
        if (result.IsFailure)
            return;
        var options = result.Value;
        await Assert.That(options.Mode).IsEqualTo(RewindMode.Files);
        await Assert.That(options.IsFiles).IsTrue();
        await Assert.That(options.SessionId).IsEqualTo("s1");
        await Assert.That(options.CheckpointId).IsEqualTo("cp1");
    }

    [Test]
    public async Task Parse_FilesFlagAfterPositionals_SelectsFilesMode()
    {
        var result = RewindOptions.Parse(["s1", "cp1", "--files"]);

        await Assert.That(result.IsSuccess).IsTrue();
        if (result.IsFailure)
            return;
        await Assert.That(result.Value.IsFiles).IsTrue();
    }

    [Test]
    public async Task Parse_FilesFlagBetweenPositionals_SelectsFilesMode()
    {
        var result = RewindOptions.Parse(["s1", "--files", "cp1"]);

        await Assert.That(result.IsSuccess).IsTrue();
        if (result.IsFailure)
            return;
        await Assert.That(result.Value.IsFiles).IsTrue();
        await Assert.That(result.Value.SessionId).IsEqualTo("s1");
        await Assert.That(result.Value.CheckpointId).IsEqualTo("cp1");
    }

    [Test]
    public async Task Parse_HelpFlags_ShortCircuit()
    {
        var longResult = RewindOptions.Parse(["--help"]);
        var shortResult = RewindOptions.Parse(["-h"]);

        await Assert.That(longResult.IsSuccess).IsTrue();
        await Assert.That(shortResult.IsSuccess).IsTrue();
        if (longResult.IsSuccess)
            await Assert.That(longResult.Value.ShowHelp).IsTrue();
        if (shortResult.IsSuccess)
            await Assert.That(shortResult.Value.ShowHelp).IsTrue();
    }

    [Test]
    public async Task Parse_MessagesFlag_SelectsMessagesMode()
    {
        var result = RewindOptions.Parse(["--messages", "s1", "cp1"]);

        await Assert.That(result.IsSuccess).IsTrue();
        if (result.IsFailure)
            return;
        var options = result.Value;
        await Assert.That(options.Mode).IsEqualTo(RewindMode.Messages);
        await Assert.That(options.IsMessages).IsTrue();
        await Assert.That(options.IsFiles).IsFalse();
        await Assert.That(options.IsBoth).IsFalse();
        await Assert.That(options.SessionId).IsEqualTo("s1");
        await Assert.That(options.CheckpointId).IsEqualTo("cp1");
    }

    [Test]
    public async Task Parse_BothFlag_SelectsBothMode()
    {
        var result = RewindOptions.Parse(["--both", "s1", "cp1"]);

        await Assert.That(result.IsSuccess).IsTrue();
        if (result.IsFailure)
            return;
        var options = result.Value;
        await Assert.That(options.Mode).IsEqualTo(RewindMode.Both);
        await Assert.That(options.IsBoth).IsTrue();
        await Assert.That(options.IsFiles).IsFalse();
        await Assert.That(options.IsMessages).IsFalse();
    }

    [Test]
    public async Task Parse_ConflictingModeFlags_AreRejected()
    {
        var filesAndMessages = RewindOptions.Parse(["--files", "--messages", "s1", "cp1"]);
        var filesAndBoth = RewindOptions.Parse(["--both", "s1", "--files", "cp1"]);
        var messagesAndBoth = RewindOptions.Parse(["--messages", "--both", "s1", "cp1"]);

        await Assert.That(filesAndMessages.IsFailure).IsTrue();
        await Assert.That(filesAndMessages.Error).Contains("Conflicting");
        await Assert.That(filesAndBoth.IsFailure).IsTrue();
        await Assert.That(filesAndBoth.Error).Contains("Conflicting");
        await Assert.That(messagesAndBoth.IsFailure).IsTrue();
        await Assert.That(messagesAndBoth.Error).Contains("Conflicting");
    }

    [Test]
    public async Task Parse_DuplicateMessagesAndBothFlags_AreRejected()
    {
        var messages = RewindOptions.Parse(["--messages", "s1", "--messages", "cp1"]);
        var both = RewindOptions.Parse(["--both", "--both", "s1", "cp1"]);

        await Assert.That(messages.IsFailure).IsTrue();
        await Assert.That(messages.Error).Contains("Duplicate --messages");
        await Assert.That(both.IsFailure).IsTrue();
        await Assert.That(both.Error).Contains("Duplicate --both");
    }

    [Test]
    public async Task Parse_UnknownFlag_IsRejected()
    {
        var result = RewindOptions.Parse(["--bogus", "s1", "cp1"]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("--bogus");
    }

    [Test]
    public async Task Parse_DuplicateFilesFlag_IsRejected()
    {
        var result = RewindOptions.Parse(["--files", "s1", "--files", "cp1"]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Duplicate --files");
    }

    [Test]
    public async Task Parse_MissingPositionals_AreRejected()
    {
        var none = RewindOptions.Parse([]);
        var one = RewindOptions.Parse(["--files", "s1"]);

        await Assert.That(none.IsFailure).IsTrue();
        await Assert.That(none.Error).Contains("<session-id>");
        await Assert.That(one.IsFailure).IsTrue();
        await Assert.That(one.Error).Contains("<checkpoint-id>");
    }

    [Test]
    public async Task Parse_ExtraPositional_IsRejected()
    {
        var result = RewindOptions.Parse(["s1", "cp1", "extra"]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("extra");
    }

    [Test]
    public async Task Parse_EmptyIds_AreRejected()
    {
        var session = RewindOptions.Parse(["", "cp1"]);
        var checkpoint = RewindOptions.Parse(["s1", "  "]);

        await Assert.That(session.IsFailure).IsTrue();
        await Assert.That(session.Error).Contains("non-empty");
        await Assert.That(checkpoint.IsFailure).IsTrue();
        await Assert.That(checkpoint.Error).Contains("non-empty");
    }

    [Test]
    public async Task PrintUsage_DocumentsAllModes()
    {
        using var writer = new StringWriter();
        RewindOptions.PrintUsage(writer);
        string usage = writer.ToString();

        await Assert.That(usage).Contains("--files");
        await Assert.That(usage).Contains("--messages");
        await Assert.That(usage).Contains("--both");
        await Assert.That(usage).Contains("<session-id>");
        await Assert.That(usage).Contains("<checkpoint-id>");
    }
}
