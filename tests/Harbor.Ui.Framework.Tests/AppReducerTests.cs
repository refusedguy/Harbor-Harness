using Harbor.Abstractions.Events;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Tests for the app-level <see cref="UiReducer.Update" />, which delegates
///     agent events to <see cref="UiReducer.Reduce" /> and handles UI messages.
/// </summary>
public class AppReducerTests
{
    [Test]
    public async Task AgentStartEvent_DelegatesToReduce_SetsRunningStatus()
    {
        var state = new UiState();
        var result = UiReducer.Update(state, new UiMsg.Agent(new AgentStartEvent("s", [])));
        await Assert.That(result.State.Status).IsEqualTo("running");
        await Assert.That(result.State.IsAgentRunning).IsTrue();
        await Assert.That(result.State.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task MessageStartEvent_DelegatesToReduce_SetsStreaming()
    {
        var state = new UiState();
        var result = UiReducer.Update(state, new UiMsg.Agent(new MessageStartEvent(AssistantMessage.Empty("s", "m"))));
        await Assert.That(result.State.IsStreaming).IsTrue();
        await Assert.That(result.State.Status).IsEqualTo("running");
    }

    [Test]
    public async Task AgentEndEvent_DelegatesToReduce_SetsIdle()
    {
        var state = new UiState { IsAgentRunning = true };
        var result = UiReducer.Update(state, new UiMsg.Agent(new AgentEndEvent([])));
        await Assert.That(result.State.Status).IsEqualTo("idle");
        await Assert.That(result.State.IsAgentRunning).IsFalse();
    }

    [Test]
    public async Task AgentErrorEvent_AddsErrorLineAndSetsStatus()
    {
        var state = new UiState();
        var result = UiReducer.Update(state, new UiMsg.Agent(new AgentErrorEvent("oops")));
        await Assert.That(result.State.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Lines[0].Role).IsEqualTo(ChatRole.Error);
        await Assert.That(result.State.Status).IsEqualTo("error");
    }

    [Test]
    public async Task KeyInput_Submit_AddsUserLineAndPromptsAgent()
    {
        var state = new UiState { Input = new InputModel("hello", [], -1) };
        var result = UiReducer.Update(state, new UiMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        await Assert.That(result.State.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Lines[0].Role).IsEqualTo(ChatRole.User);
        await Assert.That(result.State.Lines[0].Text).IsEqualTo("hello");
        await Assert.That(result.Effect).IsNotEqualTo(new TuiEffect.None());
    }

    [Test]
    public async Task Viewport_UpdatesViewportLines()
    {
        var state = new UiState();
        var result = UiReducer.Update(state, new UiMsg.Viewport(42));
        await Assert.That(result.State.ViewportLines).IsEqualTo(42);
    }

    [Test]
    public async Task TogglePanel_HidesVisiblePanel()
    {
        var state = new UiState
        {
            RegisteredPanelIds = ["p1"],
            PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty.SetItem("p1", TuiPanelState.Visible)
        };
        var result = UiReducer.Update(state, new UiMsg.TogglePanel("p1"));
        await Assert.That(result.State.PanelStates["p1"]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task ScrollResetToTail_PinsScrollAndSetsWasRunning()
    {
        var state = new UiState { ScrollOffset = 10 };
        var result = UiReducer.Update(state, UiMsg.ScrollResetToTail);
        await Assert.That(result.State.ScrollOffset).IsEqualTo(0);
        await Assert.That(result.State.WasRunning).IsTrue();
    }
}
