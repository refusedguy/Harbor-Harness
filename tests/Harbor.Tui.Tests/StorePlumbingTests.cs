using System.Collections.Immutable;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Issue #94, items 2–4: projector plumbing — deterministic streaming-tail
///     timestamps, pending-delta visibility, and the single
///     <c>ToolCallId</c> join key.
/// </summary>
public class StorePlumbingTests
{
    private static readonly DateTime FixedTs = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Project_PendingStreamText_HiddenUntilFlush()
    {
        // Flush-gated visibility (#94 vs #46): unflushed pending deltas stay
        // out of the transcript tail — projecting them would recompose the
        // whole transcript O(history) per delta. Only the synced prefix shows.
        var projector = new DefaultUiProjector();
        var state = new UiState
        {
            IsStreaming = true,
            Active = new ActiveMessage("he", string.Empty),
            PendingStreamText = ChunkedBuffer.Empty.Append("llo"),
        };

        var screen = projector.Project(state);

        var tail = screen.Transcript.Blocks.OfType<UiMessageBlock>()
            .First(b => b.Phase == MessageRenderPhase.Streaming);
        await Assert.That(string.Concat(tail.Spans.Select(s => s.Text))).IsEqualTo("he");
    }

    [Test]
    public async Task Project_PendingOnlyDelta_NotProjectedBeforeFlush()
    {
        var projector = new DefaultUiProjector();
        var state = new UiState
        {
            IsStreaming = true,
            Active = ActiveMessage.Empty,
            PendingStreamText = ChunkedBuffer.Empty.Append("unflushed"),
        };

        var screen = projector.Project(state);

        await Assert.That(screen.Transcript.Blocks.OfType<UiMessageBlock>()
            .Any(b => b.Phase == MessageRenderPhase.Streaming)).IsFalse();
    }

    [Test]
    public async Task Project_IdenticalStates_ProduceIdenticalTailTimestamps()
    {
        var projector = new DefaultUiProjector();
        var line = new ChatLine(ChatRole.User, "hi", TimestampUtc: FixedTs);
        UiState Build() => new()
        {
            Lines = ImmutableArray.Create(line),
            IsStreaming = true,
            Active = new ActiveMessage("streaming", string.Empty),
        };

        var first = projector.Project(Build());
        var second = projector.Project(Build());

        DateTime TailTs(UiScreenModel s) => s.Transcript.RenderedLines
            .First(l => l.Id == "streaming-text").TimestampUtc;

        await Assert.That(TailTs(first)).IsEqualTo(FixedTs);
        await Assert.That(TailTs(second)).IsEqualTo(FixedTs);
        await Assert.That(first.StateRevision).IsEqualTo(second.StateRevision);
    }

    [Test]
    public async Task ToolExecutionLines_AndCards_JoinOnSingleToolCallId()
    {
        var store = new UiStore();
        using var args = JsonDocument.Parse("{\"path\":\"f.txt\"}");
        store.Dispatch(new ToolExecutionStartEvent("tc_1", "read", args.RootElement));
        store.Dispatch(new ToolExecutionEndEvent("tc_1", ToolResult.Success("contents"), false));

        var lines = ToolCallKey.FindLines(store.State, "tc_1");
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(lines[1].Role).IsEqualTo(ChatRole.ToolResult);

        var projector = new DefaultUiProjector();
        var screen = projector.Project(store.State);
        var blocks = screen.Transcript.Blocks.OfType<UiMessageBlock>()
            .Where(b => ToolCallKey.Matches(b.Id, "tc_1")).ToList();
        await Assert.That(blocks.Count).IsEqualTo(2);

        // The card path keys by the same string: a card id joins the transcript.
        const string cardId = "tc_1";
        await Assert.That(ToolCallKey.Matches(cardId, lines[0].ToolCallId)).IsTrue();
    }

    [Test]
    public async Task SetLine_PreservesToolCallIdJoinKey()
    {
        var store = new UiStore();
        using var args = JsonDocument.Parse("{}");
        store.Dispatch(new ToolExecutionStartEvent("tc_9", "bash", args.RootElement));

        var edited = store.State.SetLine(0, ChatRole.Tool, "edited text");

        await Assert.That(edited.Lines[0].ToolCallId).IsEqualTo("tc_9");
        await Assert.That(ToolCallKey.FindLines(edited, "tc_9").Count).IsEqualTo(1);
    }
}
