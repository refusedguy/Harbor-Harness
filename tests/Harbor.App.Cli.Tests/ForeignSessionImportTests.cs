using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2674 F13. The foreign-history importer is pure mapping, so the tests
///     pin the projected Harbor messages (role, text, stop reason, tool args,
///     model and timestamp) rather than touching the session store or the CLI.
/// </summary>
public class ForeignSessionImportTests
{
    private const string SessionId = "imported-session-1";

    [Test]
    public async Task Parse_Claude_MapsTextMessagesWithModelAndTimestamp()
    {
        string jsonl = """
                       {"type":"summary","summary":"ignored"}
                       {"type":"user","timestamp":"2026-01-02T03:04:05Z","message":{"role":"user","content":"hello"}}
                       {"type":"assistant","timestamp":"2026-01-02T03:04:06Z","message":{"role":"assistant","content":[{"type":"text","text":"hi there"}],"model":"claude-sonnet","usage":{"input_tokens":10,"output_tokens":4}}}
                       not-json
                       """;

        var parsed = ForeignSessionImport.Parse(ForeignSessionImport.Claude, jsonl, SessionId);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Messages.Count).IsEqualTo(2);
        await Assert.That(parsed.Value.Imported).IsEqualTo(2);
        await Assert.That(parsed.Value.Skipped).IsEqualTo(2);

        var user = (UserMessage)parsed.Value.Messages[0];
        await Assert.That(user.Content).IsEqualTo("hello");
        await Assert.That(user.Model).IsEqualTo("claude-sonnet");
        await Assert.That(user.Agent).IsEqualTo("code");
        await Assert.That(user.SessionId).IsEqualTo(SessionId);
        await Assert.That(user.CreatedAt.Year).IsEqualTo(2026);

        var assistant = (AssistantMessage)parsed.Value.Messages[1];
        await Assert.That(assistant.Parts.Count).IsEqualTo(1);
        await Assert.That(((TextPart)assistant.Parts[0]).Text).IsEqualTo("hi there");
        await Assert.That(assistant.StopReason).IsEqualTo(StopReason.Stop);
        await Assert.That(assistant.Usage.InputTokens).IsEqualTo(10);
        await Assert.That(assistant.Usage.OutputTokens).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_Claude_ToolCallArgsOutliveTheSourceDocument()
    {
        string jsonl = """
                       {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"tc1","name":"read","input":{"path":"README.md","limit":10}}],"model":"claude-sonnet"}}
                       """;

        var parsed = ForeignSessionImport.Parse(ForeignSessionImport.Claude, jsonl, SessionId);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Messages.Count).IsEqualTo(1);
        var assistant = (AssistantMessage)parsed.Value.Messages[0];
        await Assert.That(assistant.StopReason).IsEqualTo(StopReason.ToolUse);
        await Assert.That(assistant.Parts.Count).IsEqualTo(1);

        var call = (ToolCallPart)assistant.Parts[0];
        await Assert.That(call.Id).IsEqualTo("tc1");
        await Assert.That(call.ToolName).IsEqualTo("read");
        await Assert.That(call.Args.GetProperty("path").GetString()).IsEqualTo("README.md");
        await Assert.That(call.Args.GetProperty("limit").GetInt32()).IsEqualTo(10);
    }

    [Test]
    public async Task Parse_Claude_HandlesThinkingAndStringAssistantContent()
    {
        string jsonl = """
                       {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"consider"},{"type":"text","text":"answer"}],"model":"claude"}}
                       {"type":"assistant","message":{"role":"assistant","content":"plain","model":"claude"}}
                       """;

        var parsed = ForeignSessionImport.Parse(ForeignSessionImport.Claude, jsonl, SessionId);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Messages.Count).IsEqualTo(2);
        await Assert.That(((ThinkingPart)((AssistantMessage)parsed.Value.Messages[0]).Parts[0]).Text).IsEqualTo("consider");
        await Assert.That(((TextPart)((AssistantMessage)parsed.Value.Messages[0]).Parts[1]).Text).IsEqualTo("answer");
        await Assert.That(((TextPart)((AssistantMessage)parsed.Value.Messages[1]).Parts[0]).Text).IsEqualTo("plain");
    }

    [Test]
    public async Task Parse_Codex_MapsResponseItemsAndUserEvents()
    {
        string jsonl = """
                       {"timestamp":"2026-01-02T03:04:05Z","type":"session_meta","payload":{"id":"orig"}}
                       {"timestamp":"2026-01-02T03:04:06Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"list files"}]}}
                       {"timestamp":"2026-01-02T03:04:07Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}
                       {"timestamp":"2026-01-02T03:04:08Z","type":"event_msg","payload":{"type":"user_message","message":"again"}}
                       """;

        var parsed = ForeignSessionImport.Parse(ForeignSessionImport.Codex, jsonl, SessionId);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Messages.Count).IsEqualTo(3);
        await Assert.That(parsed.Value.Skipped).IsEqualTo(1);
        await Assert.That(((UserMessage)parsed.Value.Messages[0]).Content).IsEqualTo("list files");
        await Assert.That(((AssistantMessage)parsed.Value.Messages[1]).Model).IsEqualTo(ForeignSessionImport.Codex);
        await Assert.That(((TextPart)((AssistantMessage)parsed.Value.Messages[1]).Parts[0]).Text).IsEqualTo("done");
        await Assert.That(((UserMessage)parsed.Value.Messages[2]).Content).IsEqualTo("again");
    }

    [Test]
    public async Task Parse_RejectsUnknownSourceAndEmptySessionId()
    {
        await Assert.That(ForeignSessionImport.Parse("cursor", "{}", SessionId).IsFailure).IsTrue();
        await Assert.That(ForeignSessionImport.Parse(ForeignSessionImport.Claude, "{}", string.Empty).IsFailure).IsTrue();
    }

    [Test]
    public async Task Parse_NonObjectJsonLinesAreCountedAsSkipped()
    {
        string jsonl = """
                       [1,2,3]
                       "just a string"
                       42
                       {"type":"user","message":{"role":"user","content":"kept"}}
                       """;

        var parsed = ForeignSessionImport.Parse(ForeignSessionImport.Claude, jsonl, SessionId);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Messages.Count).IsEqualTo(1);
        await Assert.That(parsed.Value.Skipped).IsEqualTo(3);
        await Assert.That(((UserMessage)parsed.Value.Messages[0]).Content).IsEqualTo("kept");
    }

    [Test]
    public async Task Parse_EmptyFile_ProducesNoMessages()
    {
        var parsed = ForeignSessionImport.Parse(ForeignSessionImport.Claude, "  \n\n", SessionId);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Messages.Count).IsEqualTo(0);
        await Assert.That(parsed.Value.Imported).IsEqualTo(0);
        await Assert.That(parsed.Value.Skipped).IsEqualTo(0);
    }
}
