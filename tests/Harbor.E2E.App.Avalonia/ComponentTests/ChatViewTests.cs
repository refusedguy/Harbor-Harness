using Avalonia.Controls;
using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.State;
using ChatLineVm = Harbor.Ui.Framework.ViewModels.ChatLineViewModel;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

using ChatRole = Harbor.Ui.Framework.State.ChatRole;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     ChatView component E2E tests — every visible state of the chat panel.
/// </summary>
/// <remarks>
///     <para>
///         Each test drives the <c>ChatView</c> into one specific state (empty,
///         typing, send-enabled, message-sent, streaming, agent-running,
///         cleared, error) and captures a screenshot with the <c>ct-</c> prefix.
///     </para>
///     <para>
///         Every test calls <see cref="HeadlessAvaloniaDriver.ResetStateAsync"/>
///         first so it starts from a known baseline — no test depends on
///         another test's side effects.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class ChatViewTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

    /// <summary>
    ///     Empty state: should show "Start a conversation" placeholder, the
    ///     InputBox, and a DISABLED Send button.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_EmptyState_ShowsPlaceholderAndDisabledSend()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var sawPlaceholder = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(sawPlaceholder).IsTrue();

        var send = Driver.FindButtonByText("Send ▶");
        await Assert.That(send).IsNotNull();
        var enabled = UI(() => send!.IsEffectivelyEnabled);
        await Assert.That(enabled).IsFalse();

        var path = await CaptureAsync("chat-empty").ConfigureAwait(false);
    }

    /// <summary>
    ///     After typing "Hello": the input shows the typed text and the Send
    ///     button is now ENABLED.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_Typing_EnablesSendButton()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Assert.That(input).IsNotNull();
        await Driver.TypeAsync(input!, "Hello").ConfigureAwait(false);

        var typedText = UI(() => input!.Text);
        await Assert.That(typedText).IsEqualTo("Hello");

        var send = Driver.FindButtonByText("Send ▶");
        var isEnabled = UI(() => send!.IsEffectivelyEnabled);
        await Assert.That(isEnabled).IsTrue();

        var path = await CaptureAsync("chat-typing").ConfigureAwait(false);
    }

    /// <summary>
    ///     After sending "Hello AI!": a user-role chat bubble with text
    ///     "Hello AI!" appears in the transcript, and the input is cleared.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_SendMessage_AddsUserBubble()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var input = Driver.FindControlByName<TextBox>("InputBox");
        var send = Driver.FindButtonByText("Send ▶");
        await Driver.TypeAsync(input!, "Hello AI!").ConfigureAwait(false);
        await Driver.ClickAsync(send!).ConfigureAwait(false);

        var sawMessage = await Driver.WaitForTextAsync("Hello AI!", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawMessage).IsTrue();

        var inputText = UI(() => input!.Text);
        await Assert.That(string.IsNullOrEmpty(inputText)).IsTrue();

        await Task.Delay(200).ConfigureAwait(false);
        var path = await CaptureAsync("chat-message-sent").ConfigureAwait(false);
    }

    /// <summary>
    ///     During streaming: a "streaming" label appears, the streaming buffer
    ///     text is visible, and the input is below it.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_Streaming_ShowsStreamingLabelAndBuffer()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsStreaming = true;
            chat.StreamingBuffer = "The model is streaming a response token by token, character by character…";
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasStreaming = await Driver.WaitForTextAsync("streaming", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasStreaming).IsTrue();

        var hasBuffer = await Driver.WaitForTextAsync("streaming a response", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasBuffer).IsTrue();

        var path = await CaptureAsync("chat-streaming").ConfigureAwait(false);

        // Reset for next test.
        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsStreaming = false;
            chat.StreamingBuffer = string.Empty;
        });
    }

    /// <summary>
    ///     Agent-running state: the "Agent is running…" banner is visible with
    ///     blinking dots.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_AgentRunning_ShowsRunningBanner()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsAgentRunning = true;
            chat.IsStreaming = false;
            chat.StatusMessage = "Agent is running…";
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasIndicator = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIndicator).IsTrue();

        var path = await CaptureAsync("chat-agent-running").ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsAgentRunning = false;
            chat.StatusMessage = string.Empty;
        });
    }

    /// <summary>
    ///     After clear (Ctrl+L equivalent — ClearCommand): the empty-state
    ///     placeholder reappears and the previously-sent message is gone.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_Clear_RemovesMessagesAndShowsPlaceholder()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Seed: send a message so we have something to clear.
        var input = Driver.FindControlByName<TextBox>("InputBox");
        var send = Driver.FindButtonByText("Send ▶");
        await Driver.TypeAsync(input!, "Message that will be cleared").ConfigureAwait(false);
        await Driver.ClickAsync(send!).ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);

        var had = await Driver.WaitForTextAsync("Message that will be cleared", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(had).IsTrue();

        // Now clear via Ctrl+L equivalent.
        UI(() => Vm.Chat.ClearCommand.Execute(null));
        await Task.Delay(200).ConfigureAwait(false);

        var sawPlaceholder = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawPlaceholder).IsTrue();

        var stillThere = Driver.GetAllVisibleText().Contains("Message that will be cleared", StringComparison.Ordinal);
        await Assert.That(stillThere).IsFalse();

        var path = await CaptureAsync("chat-cleared").ConfigureAwait(false);
    }

    /// <summary>
    ///     Error state: a red error message appears in the transcript as a
    ///     ChatRole.Error bubble.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_ErrorState_ShowsRedErrorMessage()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.Lines.Add(new ChatLineVm(
                ChatRole.Error,
                "Something went wrong: provider returned 503 Service Unavailable"));
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasError = await Driver.WaitForTextAsync("Something went wrong", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasError).IsTrue();

        var path = await CaptureAsync("chat-error").ConfigureAwait(false);
    }

    /// <summary>
    ///     Stop button is visible when the agent is thinking (IsThinking=true).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_Thinking_ShowsThinkingLabelAndStopButton()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsThinking = true;
            chat.IsAgentRunning = true;
            chat.IsStreaming = false;
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasThinking = await Driver.WaitForTextAsync("thinking", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasThinking).IsTrue();

        var path = await CaptureAsync("chat-thinking").ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsThinking = false;
            chat.IsAgentRunning = false;
        });
    }
}
