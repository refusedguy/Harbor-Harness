using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Tests for the composed entry point <see cref="ChatAppReducer.Update" />,
///     which delegates agent events to <see cref="ChatAppReducer.Reduce" /> and
///     hands every other message to <see cref="AppReducer.Update" />.
/// </summary>
public class AppReducerTests
{
    [Test]
    public async Task AgentStartEvent_DelegatesToReduce_SetsRunningStatus()
    {
        var state = new UiState();
        var result = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentStartEvent("s", [])));
        await Assert.That(result.State.Chat.Status).IsEqualTo("running");
        await Assert.That(result.State.Chat.IsAgentRunning).IsTrue();
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task MessageStartEvent_DelegatesToReduce_SetsStreaming()
    {
        var state = new UiState();
        var result = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new MessageStartEvent(AssistantMessage.Empty("s", "m"))));
        await Assert.That(result.State.Chat.IsStreaming).IsTrue();
        await Assert.That(result.State.Chat.Status).IsEqualTo("running");
    }

    [Test]
    public async Task AgentEndEvent_DelegatesToReduce_SetsIdle()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { IsAgentRunning = true } };
        var result = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([])));
        await Assert.That(result.State.Chat.Status).IsEqualTo("idle");
        await Assert.That(result.State.Chat.IsAgentRunning).IsFalse();
    }

    [Test]
    public async Task AgentErrorEvent_AddsErrorLineAndSetsStatus()
    {
        var state = new UiState();
        var result = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentErrorEvent("oops")));
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.Error);
        await Assert.That(result.State.Chat.Status).IsEqualTo("error");
    }

    [Test]
    public async Task KeyInput_Submit_AddsUserLineAndPromptsAgent()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { Input = new InputModel("hello", [], -1) } };
        var result = ChatAppReducer.Update(state, new AppMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.User);
        await Assert.That(result.State.Chat.Lines[0].Text).IsEqualTo("hello");
        await Assert.That(result.Effect).IsNotEqualTo(new TuiEffect.None());
    }

    [Test]
    public async Task Viewport_UpdatesViewportLines()
    {
        var state = new UiState();
        var result = ChatAppReducer.Update(state, new AppMsg.Viewport(42));
        await Assert.That(result.State.Ui.ViewportLines).IsEqualTo(42);
    }

    [Test]
    public async Task TogglePanel_HidesVisiblePanel()
    {
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                RegisteredPanelIds = ["p1"],
                PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty.SetItem("p1", TuiPanelState.Visible)
            }
        };
        var result = ChatAppReducer.Update(state, new AppMsg.TogglePanel("p1"));
        await Assert.That(result.State.Ui.PanelStates["p1"]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task ScrollResetToTail_PinsScrollAndSnapshotsWasRunning()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 10 } };
        var result = ChatAppReducer.Update(state, new AppMsg.ScrollResetToTail());
        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
        // WasRunning mirrors IsAgentRunning (rising-edge invariant) instead
        // of being forced true — see UiReducerRegressionTests.
        await Assert.That(result.State.Chat.WasRunning).IsFalse();

        var running = ChatAppReducer.Update(
            new UiState { Chat = ChatDomainState.Empty with { IsAgentRunning = true } },
            new AppMsg.ScrollResetToTail());
        await Assert.That(running.State.Chat.WasRunning).IsTrue();
    }
}
