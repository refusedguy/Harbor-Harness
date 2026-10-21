using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using Harbor.TestKit;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #92: reducer state regressions. Each test drives <see cref="ChatAppReducer.Update" />
///     through the honest path (no white-box calls) and pins the fixed behavior.
/// </summary>
public class UiReducerRegressionTests
{
    [Test]
    public async Task AgentEnd_AfterError_PreservesErrorStatus()
    {
        var state = new UiState();
        var errored = ChatAppReducer.Update(state, new ChatAppMsg.Agent(TestEvents.AgentError("oops")));
        var ended = ChatAppReducer.Update(errored.State, new ChatAppMsg.Agent(TestEvents.AgentEnd()));

        await Assert.That(ended.State.Chat.Status).IsEqualTo("error");
        await Assert.That(ended.State.Chat.IsAgentRunning).IsFalse();
    }

    [Test]
    public async Task ScrollResetToTail_PreservesRisingEdge()
    {
        // Idle store must not fabricate WasRunning — otherwise the next run
        // start produces no rising edge and renderers never snap to tail.
        var idle = ChatAppReducer.Update(new UiState(), new AppMsg.ScrollResetToTail());
        await Assert.That(idle.State.Chat.WasRunning).IsFalse();

        var running = ChatAppReducer.Update(new UiState { Chat = ChatDomainState.Empty with { IsAgentRunning = true } }, new AppMsg.ScrollResetToTail());
        await Assert.That(running.State.Chat.WasRunning).IsTrue();
    }

    [Test]
    public async Task Abort_FoldsStreamedTextBeforeClearing()
    {
        var state = new UiState();
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(TestEvents.MessageStart(AssistantMessage.Empty("s", "m")))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            TestEvents.TextDeltaUpdate("m", new string('x', 300), AssistantMessage.Empty("s", "m")))).State;

        var aborted = ChatAppReducer.Update(state, new AppMsg.KeyInput(ChatAction.Abort, new UiKey(UiKeyCode.Enter)));

        var texts = aborted.State.Chat.Lines.Select(l => l.Text).ToList();
        await Assert.That(texts.Any(t => t.Contains(new string('x', 10)))).IsTrue();
        await Assert.That(texts.Any(t => t.Contains("Aborted."))).IsTrue();
        await Assert.That(aborted.State.Chat.Active.TextBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AgentStart_ReplaysAllRoles_OnEmptyStore()
    {
        var assistant = AssistantMessage.Empty("s", "m") with
        {
            Parts = new ContentPart[] { new TextPart("hello there") }
        };
        var history = new AgentMessage[]
        {
            TestMessages.User("hi", id: "u1", sessionId: "s", model: "m"),
            assistant,
            TestMessages.ToolResult(new[] { new ToolResultEntry("tc1", "read", "file contents", false) }, id: "t1", sessionId: "s"),
        };
        var started = ChatAppReducer.Update(new UiState(), new ChatAppMsg.Agent(TestEvents.AgentStart("s", history)));

        await Assert.That(started.State.Chat.Lines.Count(l => l.Role == ChatRole.User && l.Text == "hi")).IsEqualTo(1);
        await Assert.That(started.State.Chat.Lines.Any(l => l.Role == ChatRole.Assistant && l.Text.Contains("hello there"))).IsTrue();
        await Assert.That(started.State.Chat.Lines.Any(l => l.Role == ChatRole.ToolResult && l.Text.Contains("file contents"))).IsTrue();
    }

    [Test]
    public async Task SubmitEcho_NormalPath_AddsLineOnce()
    {
        // Common order: local echo first, AgentStart replay skipped wholesale
        // on the non-empty store — still exactly one echo.
        var submitted = ChatAppReducer.Update(
            new UiState { Ui = TerminalUiState.Empty with { Input = new InputModel("hi", [], -1) } },
            new AppMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        var history = new AgentMessage[]
        {
            TestMessages.User("hi", id: "u1", sessionId: "s", model: "m"),
        };
        var started = ChatAppReducer.Update(submitted.State, new ChatAppMsg.Agent(TestEvents.AgentStart("s", history)));

        await Assert.That(started.State.Chat.Lines.Count(l => l.Role == ChatRole.User && l.Text == "hi")).IsEqualTo(1);
    }
}
