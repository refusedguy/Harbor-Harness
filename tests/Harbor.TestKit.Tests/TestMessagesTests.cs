// TestMessagesTests.cs — what the shared message builders promise (#1816).
//
// The builders are the ONE place a test builds a message, and 195 hand-written
// constructions across 71 files now go through them. Two things therefore have
// to hold, and this file is where they are pinned:
//
//   1. THE BUILDER BUILDS THE MESSAGE THE HAND-WRITTEN CALL BUILT. Every
//      parameter of every record is checked against the record the same
//      arguments produce when they are written out by hand — the values, the
//      order they land in, and their types. A builder that dropped the parent
//      id, or put the model where the agent goes, would change the meaning of
//      every call site that relies on it.
//
//   2. THE DEFAULTS ARE THE VALUES THE DOCS PROMISE. The defaults are the
//      measured mass values of the tree the migration ran on; a builder that
//      silently substituted a different session id or a different agent would
//      change the meaning of every call site that relies on the default.

using Harbor.Abstractions.Models;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The shared message builders: the message they build, and the defaults
///     they promise.
/// </summary>
public class TestMessagesTests
{
    [Test]
    public async Task A_User_Message_Is_The_One_The_Hand_Written_Call_Built()
    {
        var attachments = new[] { new ImageAttachment("/tmp/a.png", "image/png", 4, 2, new byte[] { 1, 2 }) };
        var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        UserMessage built = TestMessages.User(
            "hi",
            id: "m1",
            sessionId: "s1",
            createdAt: when,
            agent: "explore",
            model: "claude-opus-4",
            parentId: "p1",
            attachments: attachments);
        UserMessage handwritten = new(
            "m1",
            "s1",
            when,
            "hi",
            "explore",
            "claude-opus-4",
            "p1",
            attachments);

        await Assert.That(built).IsEqualTo(handwritten)
            .Because("the builder is a factory for the very record the product persists, and a difference in any field is a difference in what the test means");
        await Assert.That(built.Id).IsEqualTo("m1");
        await Assert.That(built.SessionId).IsEqualTo("s1");
        await Assert.That(built.CreatedAt).IsEqualTo(when);
        await Assert.That(built.Content).IsEqualTo("hi");
        await Assert.That(built.Agent).IsEqualTo("explore");
        await Assert.That(built.Model).IsEqualTo("claude-opus-4");
        await Assert.That(built.ParentId).IsEqualTo("p1");
        await Assert.That(built.Attachments).IsNotNull();
        await Assert.That(built.Attachments!.Count).IsEqualTo(1);
        await Assert.That(built.Attachments![0].Path).IsEqualTo("/tmp/a.png");
        await Assert.That(built.Role).IsEqualTo("user");
    }

    [Test]
    public async Task A_User_Message_Takes_The_Defaults_The_Docs_Promise()
    {
        UserMessage msg = TestMessages.User("hi");

        await Assert.That(msg.SessionId).IsEqualTo("session-1")
            .Because("the default session id is the one the kit already used, so the call sites written before the migration still mean what they meant");
        await Assert.That(msg.Agent).IsEqualTo("code")
            .Because("107 of the 122 hand-written user messages already named the agent \"code\"");
        await Assert.That(msg.Model).IsEqualTo("test-model")
            .Because("39 of the 122 hand-written user messages already named the model \"test-model\", the largest single value");
        await Assert.That(msg.ParentId).IsNull();
        await Assert.That(msg.Attachments).IsNull();
    }

    [Test]
    public async Task A_User_Message_Gets_A_Fresh_Id_And_A_Now_Timestamp()
    {
        UserMessage first = TestMessages.User("hi");
        UserMessage second = TestMessages.User("hi");

        await Assert.That(first.Id).IsNotEqualTo(second.Id)
            .Because("a message id identifies one message, and a builder that handed out the same id twice would make two messages look like one");
        await Assert.That(first.Id.Length).IsEqualTo(32)
            .Because("the id is the guid's N format, the one the tree's hand-written calls already used");
        await Assert.That(Math.Abs((first.CreatedAt - second.CreatedAt).TotalMilliseconds)).IsLessThan(5_000)
            .Because("the default timestamp is now, and two calls a moment apart are a moment apart");
    }

    [Test]
    public async Task An_Assistant_Message_Is_The_One_The_Hand_Written_Call_Built()
    {
        var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var usage = new Usage(10, 5);

        AssistantMessage built = TestMessages.Assistant(
            "hi",
            id: "a1",
            sessionId: "s1",
            createdAt: when,
            stopReason: StopReason.ToolUse,
            usage: usage,
            model: "claude-opus-4",
            parentId: "p1",
            isSummary: true,
            summaryFirstKeptId: "m1");
        AssistantMessage handwritten = new(
            "a1",
            "s1",
            when,
            [new TextPart("hi")],
            StopReason.ToolUse,
            usage,
            "claude-opus-4",
            "p1",
            true,
            "m1");

        await Assert.That(built.Id).IsEqualTo(handwritten.Id);
        await Assert.That(built.SessionId).IsEqualTo(handwritten.SessionId);
        await Assert.That(built.CreatedAt).IsEqualTo(handwritten.CreatedAt);
        await Assert.That(built.Parts.Count).IsEqualTo(handwritten.Parts.Count)
            .Because("the text form carries exactly one text part, which is what the hand-written call carried");
        await Assert.That(built.Parts[0]).IsTypeOf<TextPart>();
        await Assert.That(((TextPart)built.Parts[0]).Text).IsEqualTo(((TextPart)handwritten.Parts[0]).Text);
        await Assert.That(built.StopReason).IsEqualTo(handwritten.StopReason);
        await Assert.That(built.Usage).IsEqualTo(handwritten.Usage);
        await Assert.That(built.Model).IsEqualTo(handwritten.Model);
        await Assert.That(built.ParentId).IsEqualTo(handwritten.ParentId);
        await Assert.That(built.IsSummary).IsEqualTo(handwritten.IsSummary);
        await Assert.That(built.SummaryFirstKeptId).IsEqualTo(handwritten.SummaryFirstKeptId);
        await Assert.That(built.Role).IsEqualTo("assistant");

        // The two messages are NOT compared as whole records, and the reason
        // is the record's own equality: a record compares its list members by
        // REFERENCE, and the two messages carry two different arrays. The
        // fields above are what that comparison would have said.
        await Assert.That(built.StopReason).IsEqualTo(StopReason.ToolUse);
        await Assert.That(built.Usage).IsEqualTo(usage);
        await Assert.That(built.Model).IsEqualTo("claude-opus-4");
        await Assert.That(built.ParentId).IsEqualTo("p1");
        await Assert.That(built.IsSummary).IsTrue();
        await Assert.That(built.SummaryFirstKeptId).IsEqualTo("m1");
    }

    [Test]
    public async Task An_Assistant_Message_Takes_The_Defaults_The_Docs_Promise()
    {
        AssistantMessage msg = TestMessages.Assistant("hi");

        await Assert.That(msg.SessionId).IsEqualTo("session-1");
        await Assert.That(msg.Model).IsEqualTo("test-model");
        await Assert.That(msg.StopReason).IsEqualTo(StopReason.Stop)
            .Because("31 of the 53 hand-written assistant messages already said StopReason.Stop");
        await Assert.That(msg.Usage).IsEqualTo(new Usage(0, 0))
            .Because("19 of the 53 hand-written assistant messages already said new Usage(0, 0)");
        await Assert.That(msg.ParentId).IsNull();
        await Assert.That(msg.IsSummary).IsFalse();
        await Assert.That(msg.SummaryFirstKeptId).IsNull();
    }

    [Test]
    public async Task An_Assistant_Message_Carries_The_Parts_It_Was_Given()
    {
        ContentPart[] parts =
        [
            new TextPart("hello"),
            new ThinkingPart("hmm"),
            new FilePart("a.png", "image/png", 10),
        ];

        AssistantMessage msg = TestMessages.AssistantParts(parts, id: "a1", sessionId: "s1");

        await Assert.That(msg.Parts.Count).IsEqualTo(3);
        await Assert.That(msg.Parts[0]).IsTypeOf<TextPart>();
        await Assert.That(msg.Parts[1]).IsTypeOf<ThinkingPart>();
        await Assert.That(msg.Parts[2]).IsTypeOf<FilePart>();
        await Assert.That(msg.Parts[2].Type).IsEqualTo("file");
        await Assert.That(msg.Id).IsEqualTo("a1");
        await Assert.That(msg.SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task A_Tool_Result_Message_Is_The_One_The_Hand_Written_Call_Built()
    {
        var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new[] { new ToolResultEntry("tc1", "read", "file contents", false) };

        ToolResultMessage built = TestMessages.ToolResult(
            entries,
            id: "t1",
            sessionId: "s1",
            createdAt: when,
            parentId: "p1");
        ToolResultMessage handwritten = new("t1", "s1", when, entries, "p1");

        await Assert.That(built).IsEqualTo(handwritten)
            .Because("the builder is a factory for the very record the product persists, and a difference in any field is a difference in what the test means");
        await Assert.That(built.Id).IsEqualTo("t1");
        await Assert.That(built.SessionId).IsEqualTo("s1");
        await Assert.That(built.CreatedAt).IsEqualTo(when);
        await Assert.That(built.Results).IsEqualTo(entries);
        await Assert.That(built.ParentId).IsEqualTo("p1");
        await Assert.That(built.Role).IsEqualTo("tool_result");
    }

    [Test]
    public async Task A_Tool_Result_Message_Takes_The_Defaults_The_Docs_Promise()
    {
        ToolResultMessage msg = TestMessages.ToolResult([]);

        await Assert.That(msg.SessionId).IsEqualTo("session-1");
        await Assert.That(msg.Results).IsEmpty();
        await Assert.That(msg.ParentId).IsNull();
    }

    [Test]
    public async Task Every_Builder_Returns_A_Fresh_Id()
    {
        await Assert.That(TestMessages.User("hi").Id).IsNotEqualTo(TestMessages.User("hi").Id);
        await Assert.That(TestMessages.Assistant("hi").Id).IsNotEqualTo(TestMessages.Assistant("hi").Id);
        await Assert.That(TestMessages.AssistantParts([]).Id).IsNotEqualTo(TestMessages.AssistantParts([]).Id);
        await Assert.That(TestMessages.ToolResult([]).Id).IsNotEqualTo(TestMessages.ToolResult([]).Id);
    }
}
