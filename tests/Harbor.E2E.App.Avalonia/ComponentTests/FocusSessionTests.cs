using Avalonia.Controls;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Focus-session overlay component E2E tests — the fullscreen focus mode:
///     toggle opens the chrome ("Focus Mode" + session title + exit hint) and
///     the Exit button closes it back to the chat view.
/// </summary>
/// <remarks>
///     <para>
///         The overlay stays attached to the visual tree while collapsed, so
///         every visibility assertion uses the visibility-honest
///         <c>WaitForRenderedTextAsync</c>/<c>GetRenderedText</c> probes, never
///         the unfiltered walk.
///     </para>
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class FocusSessionTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("FocusSession").ConfigureAwait(false);

    /// <summary>
    ///     Toggling focus session opens the chrome (title + exit hint) with
    ///     the session title populated, and toggling again closes it.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task FocusSession_Toggle_ShowsChromeAndCloses()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        UI(() => Vm.IsFocusSessionOpen = false);

        UI(() => Vm.ToggleFocusSessionCommand.Execute(null));

        var opened = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.IsFocusSessionOpen), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(opened).IsTrue();

        var hasTitle = await Driver.WaitForRenderedTextAsync("Focus Mode", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasTitle).IsTrue();

        var hasHint = await Driver.WaitForRenderedTextAsync("Esc to exit", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasHint).IsTrue();

        var sessionTitle = UI(() => Vm.FocusSession.Title);
        await Assert.That(sessionTitle).IsNotEqualTo(string.Empty);

        UI(() => Vm.ToggleFocusSessionCommand.Execute(null));
        var closed = UI(() => Vm.IsFocusSessionOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("focus-toggle").ConfigureAwait(false);
    }

    /// <summary>
    ///     Clicking the "Exit Focus Mode" button closes the overlay and the
    ///     chrome disappears from the rendered tree.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task FocusSession_ExitButton_ClosesOverlay()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        UI(() => Vm.IsFocusSessionOpen = false);

        UI(() => Vm.ToggleFocusSessionCommand.Execute(null));
        var opened = await Driver.WaitForRenderedTextAsync("Focus Mode", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(opened).IsTrue();

        var exit = Driver.FindButtonByText("Exit Focus Mode");
        await Assert.That(exit).IsNotNull();
        await Driver.ClickAsync(exit!).ConfigureAwait(false);

        var closed = await Driver.WaitForConditionAsync(
            () => UI(() => !Vm.IsFocusSessionOpen), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(closed).IsTrue();

        var chromeGone = await Driver.WaitForConditionAsync(
            () => !UI(() => Driver.GetRenderedText().Contains("Focus Mode", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(chromeGone).IsTrue();

        await CaptureAsync("focus-exit-button").ConfigureAwait(false);
    }
}
