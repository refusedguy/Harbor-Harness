// TranscriptInspectorTests.cs — the pure inspection of a transcript (#1744).
//
// The inspection is what tells a reader that a transcript is not whole: which
// tool calls it never closed, and how to close them in memory. It is pure
// structure — no file, no store, no clock — so its rules are pinned here as
// behaviour rather than as prose.
//
// The rules:
//
//   1. a tool call with no answer is UNCLOSED — it is named, not hidden;
//   2. closing it puts the answer right after the assistant message that
//      asked, so the transcript reads as a balanced one;
//   3. the closing is DETERMINISTIC — the same transcript closes to the same
//      one, every time;
//   4. a transcript with nothing to close is handed back untouched.

using System.Text.Json;
using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Tests;

public class TranscriptInspectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static ToolCallPart Call(string id, string toolName) =>
        new(id, toolName, JsonDocument.Parse("{}").RootElement.Clone());

    private static AssistantMessage Asking(params ToolCallPart[] calls) => new(
        "a1",
        "session-1",
        T0,
        calls,
        StopReason.ToolUse,
        new Usage(1, 1),
        "test-model");

    private static UserMessage User(string id, string content) =>
        new(id, "session-1", T0, content, "code", "test-model");

    private static ToolResultMessage Answer(string toolCallId, string toolName) => new(
        "r1",
        "session-1",
        T0,
        [new ToolResultEntry(toolCallId, toolName, "file contents", false)]);

    /// <summary>
    ///     The unanswered call is named — the transcript is not silently
    ///     "complete" when a turn was cut in half.
    /// </summary>
    [Test]
    public async Task FindUnclosedToolCalls_NamesTheCallNobodyAnswered()
    {
        IReadOnlyList<AgentMessage> transcript =
        [
            User("u1", "read the file"),
            Asking(Call("call-1", "read")),
        ];

        var unclosed = TranscriptInspector.FindUnclosedToolCalls(transcript);

        await Assert.That(unclosed.Count).IsEqualTo(1);
        await Assert.That(unclosed[0].Id).IsEqualTo("call-1");
        await Assert.That(unclosed[0].ToolName).IsEqualTo("read");
    }

    /// <summary>
    ///     A call that WAS answered is not reported: a duplicated answer is as
    ///     invalid for a provider as a missing one.
    /// </summary>
    [Test]
    public async Task FindUnclosedToolCalls_IgnoresACallThatWasAnswered()
    {
        IReadOnlyList<AgentMessage> transcript =
        [
            User("u1", "read the file"),
            Asking(Call("call-1", "read")),
            Answer("call-1", "read"),
        ];

        var unclosed = TranscriptInspector.FindUnclosedToolCalls(transcript);

        await Assert.That(unclosed).IsEmpty();
    }

    /// <summary>
    ///     The closing sits right after the assistant message that asked, so
    ///     the transcript a reader gets is balanced and reads in order.
    /// </summary>
    [Test]
    public async Task CloseUnclosedToolCalls_PutsTheAnswerRightAfterTheQuestion()
    {
        IReadOnlyList<AgentMessage> transcript =
        [
            User("u1", "read the file"),
            Asking(Call("call-1", "read")),
        ];

        var closed = TranscriptInspector.CloseUnclosedToolCalls(transcript);

        await Assert.That(closed.Count).IsEqualTo(3);
        await Assert.That(closed[1]).IsTypeOf<AssistantMessage>();
        var closer = closed[2] as ToolResultMessage;
        await Assert.That(closer).IsNotNull();
        await Assert.That(closer!.Results.Count).IsEqualTo(1);
        await Assert.That(closer.Results[0].ToolCallId).IsEqualTo("call-1");
        await Assert.That(closer.Results[0].ToolName).IsEqualTo("read");
        // The answer carries the outcome-unknown code: the call may have run,
        // so a blind retry is not safe.
        await Assert.That(closer.Results[0].IsError).IsTrue();
        await Assert.That(closer.Results[0].Output).Contains(UnresolvedToolCall.OutcomeUnknownCode);
        // The closer points back at the assistant message it closes.
        await Assert.That(closer.ParentId).IsEqualTo("a1");
    }

    /// <summary>
    ///     The closing is deterministic: the same transcript closes to the same
    ///     transcript, every time. A read that answered differently on two
    ///     reads of one file would be a read nobody could reason about.
    /// </summary>
    [Test]
    public async Task CloseUnclosedToolCalls_IsDeterministic()
    {
        IReadOnlyList<AgentMessage> transcript =
        [
            User("u1", "read the file"),
            Asking(Call("call-1", "read")),
        ];

        var first = TranscriptInspector.CloseUnclosedToolCalls(transcript);
        var second = TranscriptInspector.CloseUnclosedToolCalls(transcript);

        await Assert.That(first.Count).IsEqualTo(second.Count);
        await Assert.That(first[2].Id).IsEqualTo(second[2].Id);
        await Assert.That(((ToolResultMessage)first[2]).Results[0].Output)
            .IsEqualTo(((ToolResultMessage)second[2]).Results[0].Output);
    }

    /// <summary>
    ///     A transcript with nothing to close is handed back untouched — the
    ///     healthy path allocates nothing and changes nothing.
    /// </summary>
    [Test]
    public async Task CloseUnclosedToolCalls_LeavesAClosedTranscriptAlone()
    {
        IReadOnlyList<AgentMessage> transcript =
        [
            User("u1", "read the file"),
            Asking(Call("call-1", "read")),
            Answer("call-1", "read"),
        ];

        var closed = TranscriptInspector.CloseUnclosedToolCalls(transcript);

        await Assert.That(closed.Count).IsEqualTo(3);
        await Assert.That(closed[2]).IsTypeOf<ToolResultMessage>();
        await Assert.That(((ToolResultMessage)closed[2]).Results[0].Output).IsEqualTo("file contents");
    }
}
