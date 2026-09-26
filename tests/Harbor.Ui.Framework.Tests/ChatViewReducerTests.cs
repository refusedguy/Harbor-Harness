using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Reducers;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Pure-reducer tests for <see cref="ChatViewReducer" />.
/// </summary>
public class ChatViewReducerTests
{
    [Test]
    public async Task MessageStartEvent_SetsIsStreamingToTrue()
    {
        var state = new ChatViewState();
        var result = ChatViewReducer.Reduce(new MessageStartEvent(AssistantMessage.Empty("s", "m")), state);
        await Assert.That(result.IsStreaming).IsTrue();
    }

    [Test]
    public async Task MessageStartEvent_ClearsStreamingBuffer()
    {
        var state = new ChatViewState { StreamingBuffer = "partial text" };
        var result = ChatViewReducer.Reduce(new MessageStartEvent(AssistantMessage.Empty("s", "m")), state);
        await Assert.That(result.StreamingBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TextDeltaEvent_AppendsToStreamingBuffer()
    {
        var state = new ChatViewState();
        var textDelta = new TextDeltaEvent("id", "hello");
        var msgUpdate = new MessageUpdateEvent(textDelta, AssistantMessage.Empty("s", "m"));
        var result = ChatViewReducer.Reduce(msgUpdate, state);
        await Assert.That(result.StreamingBuffer).IsEqualTo("hello");
    }

    [Test]
    public async Task ThinkingDeltaEvent_SetsIsThinkingAndAppendsBuffer()
    {
        var state = new ChatViewState();
        var thinkDelta = new ThinkingDeltaEvent("id", "reasoning");
        var msgUpdate = new MessageUpdateEvent(thinkDelta, AssistantMessage.Empty("s", "m"));
        var result = ChatViewReducer.Reduce(msgUpdate, state);
        await Assert.That(result.IsThinking).IsTrue();
        await Assert.That(result.StreamingBuffer).IsEqualTo("reasoning");
    }

    [Test]
    public async Task ToolCallStartEvent_AddsToolCall()
    {
        var state = new ChatViewState();
        var tcStart = new ToolCallStartEvent("tc1", "read");
        var msgUpdate = new MessageUpdateEvent(tcStart, AssistantMessage.Empty("s", "m"));
        var result = ChatViewReducer.Reduce(msgUpdate, state);
        await Assert.That(result.ToolCalls.Length).IsEqualTo(1);
        await Assert.That(result.ToolCalls[0].ToolName).IsEqualTo("read");
        await Assert.That(result.ToolCalls[0].Status).IsEqualTo("running");
    }

    [Test]
    public async Task MessageEndEvent_WithBuffer_AddsLineAndClearsStreaming()
    {
        var state = new ChatViewState { StreamingBuffer = "final text", IsThinking = false };
        var result = ChatViewReducer.Reduce(new MessageEndEvent(AssistantMessage.Empty("s", "m")), state);
        await Assert.That(result.Lines.Length).IsEqualTo(1);
        await Assert.That(result.Lines[0].Text).IsEqualTo("final text");
        await Assert.That(result.IsStreaming).IsFalse();
        await Assert.That(result.StreamingBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task MessageEndEvent_EmptyBuffer_NoLineAdded()
    {
        var state = new ChatViewState { StreamingBuffer = string.Empty };
        var result = ChatViewReducer.Reduce(new MessageEndEvent(AssistantMessage.Empty("s", "m")), state);
        await Assert.That(result.Lines.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AgentStartEvent_ResetsAllFields()
    {
        var state = new ChatViewState
        {
            Lines = [new ChatLineViewModel(ChatRole.User, "old")],
            IsStreaming = true,
            IsThinking = true,
            StreamingBuffer = "partial",
            ToolCalls = [new ToolCallViewModel("tc", "tool", "", "running", "", TimeSpan.Zero, false, false)],
            PullProgress = 0.5,
            ShowPullIndicator = true,
            ContentScale = 1.5
        };
        var result = ChatViewReducer.Reduce(new AgentStartEvent("s", []), state);
        await Assert.That(result.Lines.Length).IsEqualTo(0);
        await Assert.That(result.IsStreaming).IsFalse();
        await Assert.That(result.IsThinking).IsFalse();
        await Assert.That(result.IsAgentRunning).IsTrue();
        await Assert.That(result.StreamingBuffer).IsEqualTo(string.Empty);
        await Assert.That(result.ToolCalls.Length).IsEqualTo(0);
        await Assert.That(result.PullProgress).IsEqualTo(0.0);
        await Assert.That(result.ShowPullIndicator).IsFalse();
        await Assert.That(result.ContentScale).IsEqualTo(1.0);
    }

    [Test]
    public async Task SessionChangedEvent_ResetsAllFields()
    {
        var state = new ChatViewState
        {
            Lines = [new ChatLineViewModel(ChatRole.User, "old")],
            IsAgentRunning = true,
            StreamingBuffer = "partial"
        };
        var result = ChatViewReducer.Reduce(new SessionChangedEvent("new-session"), state);
        await Assert.That(result.Lines.Length).IsEqualTo(0);
        await Assert.That(result.IsAgentRunning).IsFalse();
        await Assert.That(result.StreamingBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AgentEndEvent_SetsIsAgentRunningToFalse()
    {
        var state = new ChatViewState { IsAgentRunning = true };
        var result = ChatViewReducer.Reduce(new AgentEndEvent([]), state);
        await Assert.That(result.IsAgentRunning).IsFalse();
    }

    [Test]
    public async Task ToolExecutionEndEvent_SetsSuccessStatus()
    {
        var state = new ChatViewState
        {
            ToolCalls = [new ToolCallViewModel("tc1", "read", "", "running", "", TimeSpan.Zero, false, false)]
        };
        var result = ChatViewReducer.Reduce(
            new ToolExecutionEndEvent("tc1", ToolResult.Success("ok"), false),
            state);
        await Assert.That(result.ToolCalls[0].Status).IsEqualTo("success");
    }

    [Test]
    public async Task ToolExecutionEndEvent_SetsErrorStatus()
    {
        var state = new ChatViewState
        {
            ToolCalls = [new ToolCallViewModel("tc1", "read", "", "running", "", TimeSpan.Zero, false, false)]
        };
        var result = ChatViewReducer.Reduce(
            new ToolExecutionEndEvent("tc1", ToolResult.Error("fail"), true),
            state);
        await Assert.That(result.ToolCalls[0].Status).IsEqualTo("error");
    }

    [Test]
    public async Task UnknownEvent_ReturnsSameStateReference()
    {
        var state = new ChatViewState { IsStreaming = true };
        var result = ChatViewReducer.Reduce(new TurnStartEvent(1), state);
        await Assert.That(result.IsStreaming).IsTrue();
        await Assert.That(ReferenceEquals(state, result)).IsTrue();
    }
}
