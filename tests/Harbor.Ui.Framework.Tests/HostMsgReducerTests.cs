using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Reducer coverage for the host-driven TEA messages that replaced the
///     <c>UiStore.Transition</c> escape hatch (§FP-007): AgentStarted, AgentEnded,
///     StatusChanged, AppendLine, InputText, Quit.
/// </summary>
public class HostMsgReducerTests
{
    [Test]
    public async Task AgentStarted_MarksRunInProgress()
    {
        var result = UiReducer.Update(new UiState(), new UiMsg.AgentStarted());
        await Assert.That(result.State.IsAgentRunning).IsTrue();
        await Assert.That(result.State.Status).IsEqualTo("running");
    }

    [Test]
    public async Task AgentEnded_NoStatus_PreservesErrorStatus()
    {
        var state = new UiState { IsAgentRunning = true, Status = "error" };
        var result = UiReducer.Update(state, new UiMsg.AgentEnded());
        await Assert.That(result.State.IsAgentRunning).IsFalse();
        await Assert.That(result.State.IsStreaming).IsFalse();
        await Assert.That(result.State.Active).IsEqualTo(ActiveMessage.Empty);
        await Assert.That(result.State.Status).IsEqualTo("error");
    }

    [Test]
    public async Task AgentEnded_NoStatus_FallsBackToIdle()
    {
        var state = new UiState { IsAgentRunning = true, Status = "running" };
        var result = UiReducer.Update(state, new UiMsg.AgentEnded());
        await Assert.That(result.State.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task AgentEnded_WithError_AddsErrorLineAndErrorStatus()
    {
        var result = UiReducer.Update(new UiState(), new UiMsg.AgentEnded("error", "boom"));
        await Assert.That(result.State.Status).IsEqualTo("error");
        await Assert.That(result.State.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Lines[0].Role).IsEqualTo(ChatRole.Error);
        await Assert.That(result.State.Lines[0].Text).IsEqualTo("boom");
    }

    [Test]
    public async Task StatusChanged_SetsStatusOnly()
    {
        var state = new UiState { IsAgentRunning = true, Status = "running" };
        var result = UiReducer.Update(state, new UiMsg.StatusChanged("idle"));
        await Assert.That(result.State.Status).IsEqualTo("idle");
        await Assert.That(result.State.IsAgentRunning).IsTrue();
    }

    [Test]
    public async Task AppendLine_AppendsRoleAndToolCallId()
    {
        var result = UiReducer.Update(new UiState(), new UiMsg.AppendLine(ChatRole.System, "note", "tc-1"));
        await Assert.That(result.State.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Lines[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(result.State.Lines[0].Text).IsEqualTo("note");
        await Assert.That(result.State.Lines[0].ToolCallId).IsEqualTo("tc-1");
    }

    [Test]
    public async Task InputText_ReplacesInputBoxContent()
    {
        var result = UiReducer.Update(new UiState(), new UiMsg.InputText("/models"));
        await Assert.That(result.State.Input.Text).IsEqualTo("/models");
    }

    [Test]
    public async Task Quit_SetsShouldQuitFlag()
    {
        var result = UiReducer.Update(new UiState(), new UiMsg.Quit());
        await Assert.That(result.State.ShouldQuit).IsTrue();
    }
}
