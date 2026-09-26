using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #92: reducer state regressions. Each test drives <see cref="UiReducer.Update" />
///     through the honest path (no white-box calls) and pins the fixed behavior.
/// </summary>
public class UiReducerRegressionTests
{
    [Test]
    public async Task AgentEnd_AfterError_PreservesErrorStatus()
    {
        var state = new UiState();
        var errored = UiReducer.Update(state, new UiMsg.Agent(new AgentErrorEvent("oops")));
        var ended = UiReducer.Update(errored.State, new UiMsg.Agent(new AgentEndEvent([])));

        await Assert.That(ended.State.Status).IsEqualTo("error");
        await Assert.That(ended.State.IsAgentRunning).IsFalse();
    }

    [Test]
    public async Task ScrollResetToTail_PreservesRisingEdge()
    {
        // Idle store must not fabricate WasRunning — otherwise the next run
        // start produces no rising edge and renderers never snap to tail.
        var idle = UiReducer.Update(new UiState(), new UiMsg.ScrollResetToTail());
        await Assert.That(idle.State.WasRunning).IsFalse();

        var running = UiReducer.Update(new UiState { IsAgentRunning = true }, new UiMsg.ScrollResetToTail());
        await Assert.That(running.State.WasRunning).IsTrue();
    }

    [Test]
    public async Task Abort_FoldsStreamedTextBeforeClearing()
    {
        var state = new UiState();
        state = UiReducer.Update(state, new UiMsg.Agent(new MessageStartEvent(AssistantMessage.Empty("s", "m")))).State;
        state = UiReducer.Update(state, new UiMsg.Agent(
            new MessageUpdateEvent(new TextDeltaEvent("m", new string('x', 300)), AssistantMessage.Empty("s", "m")))).State;

        var aborted = UiReducer.Update(state, new UiMsg.KeyInput(ChatAction.Abort, new UiKey(UiKeyCode.Enter)));

        var texts = aborted.State.Lines.Select(l => l.Text).ToList();
        await Assert.That(texts.Any(t => t.Contains(new string('x', 10)))).IsTrue();
        await Assert.That(texts.Any(t => t.Contains("Aborted."))).IsTrue();
        await Assert.That(aborted.State.Active.TextBuffer).IsEqualTo(string.Empty);
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
            new UserMessage("u1", "s", DateTimeOffset.UtcNow, "hi", "code", "m", null),
            assistant,
            new ToolResultMessage("t1", "s", DateTimeOffset.UtcNow,
                new[] { new ToolResultEntry("tc1", "read", "file contents", false) }, null),
        };
        var started = UiReducer.Update(new UiState(), new UiMsg.Agent(new AgentStartEvent("s", history)));

        await Assert.That(started.State.Lines.Count(l => l.Role == ChatRole.User && l.Text == "hi")).IsEqualTo(1);
        await Assert.That(started.State.Lines.Any(l => l.Role == ChatRole.Assistant && l.Text.Contains("hello there"))).IsTrue();
        await Assert.That(started.State.Lines.Any(l => l.Role == ChatRole.ToolResult && l.Text.Contains("file contents"))).IsTrue();
    }

    [Test]
    public async Task SubmitEcho_NormalPath_AddsLineOnce()
    {
        // Common order: local echo first, AgentStart replay skipped wholesale
        // on the non-empty store — still exactly one echo.
        var submitted = UiReducer.Update(
            new UiState { Input = new InputModel("hi", [], -1) },
            new UiMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        var history = new AgentMessage[]
        {
            new UserMessage("u1", "s", DateTimeOffset.UtcNow, "hi", "code", "m", null),
        };
        var started = UiReducer.Update(submitted.State, new UiMsg.Agent(new AgentStartEvent("s", history)));

        await Assert.That(started.State.Lines.Count(l => l.Role == ChatRole.User && l.Text == "hi")).IsEqualTo(1);
    }
}
