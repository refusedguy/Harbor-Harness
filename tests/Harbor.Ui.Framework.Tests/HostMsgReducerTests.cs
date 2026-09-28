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
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.AgentStarted());
        await Assert.That(result.State.Chat.IsAgentRunning).IsTrue();
        await Assert.That(result.State.Chat.Status).IsEqualTo("running");
    }

    [Test]
    public async Task AgentEnded_NoStatus_PreservesErrorStatus()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { IsAgentRunning = true, Status = "error" } };
        var result = ChatAppReducer.Update(state, new ChatAppMsg.AgentEnded());
        await Assert.That(result.State.Chat.IsAgentRunning).IsFalse();
        await Assert.That(result.State.Chat.IsStreaming).IsFalse();
        await Assert.That(result.State.Chat.Active).IsEqualTo(ActiveMessage.Empty);
        await Assert.That(result.State.Chat.Status).IsEqualTo("error");
    }

    [Test]
    public async Task AgentEnded_NoStatus_FallsBackToIdle()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { IsAgentRunning = true, Status = "running" } };
        var result = ChatAppReducer.Update(state, new ChatAppMsg.AgentEnded());
        await Assert.That(result.State.Chat.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task AgentEnded_WithError_AddsErrorLineAndErrorStatus()
    {
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.AgentEnded("error", "boom"));
        await Assert.That(result.State.Chat.Status).IsEqualTo("error");
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.Error);
        await Assert.That(result.State.Chat.Lines[0].Text).IsEqualTo("boom");
    }

    [Test]
    public async Task StatusChanged_SetsStatusOnly()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { IsAgentRunning = true, Status = "running" } };
        var result = ChatAppReducer.Update(state, new ChatAppMsg.StatusChanged("idle"));
        await Assert.That(result.State.Chat.Status).IsEqualTo("idle");
        await Assert.That(result.State.Chat.IsAgentRunning).IsTrue();
    }

    [Test]
    public async Task AppendLine_AppendsRoleAndToolCallId()
    {
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.AppendLine(ChatRole.System, "note", "tc-1"));
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(result.State.Chat.Lines[0].Text).IsEqualTo("note");
        await Assert.That(result.State.Chat.Lines[0].ToolCallId).IsEqualTo("tc-1");
    }

    [Test]
    public async Task InputText_ReplacesInputBoxContent()
    {
        var result = ChatAppReducer.Update(new UiState(), new AppMsg.InputText("/models"));
        await Assert.That(result.State.Ui.Input.Text).IsEqualTo("/models");
    }

    [Test]
    public async Task Quit_SetsShouldQuitFlag()
    {
        var result = ChatAppReducer.Update(new UiState(), new AppMsg.Quit());
        await Assert.That(result.State.Ui.ShouldQuit).IsTrue();
    }

    [Test]
    public async Task Reset_ReturnsFreshState()
    {
        var dirty = ChatAppReducer.Update(new UiState(), new ChatAppMsg.ConfigureRuntime("m", "p", "code")).State;
        dirty = ChatAppReducer.Update(dirty, new ChatAppMsg.AppendLine(ChatRole.User, "hi")).State;

        var result = ChatAppReducer.Update(dirty, new AppMsg.Reset());

        await Assert.That(result.State).IsEqualTo(new UiState());
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task ConfigureRuntime_BindsSessionChrome()
    {
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.ConfigureRuntime("m", "p", "code"));
        await Assert.That(result.State.Chat.Model).IsEqualTo("m");
        await Assert.That(result.State.Chat.Provider).IsEqualTo("p");
        await Assert.That(result.State.Chat.AgentName).IsEqualTo("code");
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(0);
    }
}
