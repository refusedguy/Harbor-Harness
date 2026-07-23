using Harbor.App.Avalonia.Services;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     ToastNotifications component E2E tests — every kind + stacking + auto-dismiss.
/// </summary>
/// <remarks>
///     <para>
///         Tests cover: info toast (blue), success toast (green), warning toast
///         (peach), error toast (red), multiple stacked toasts, auto-dismiss
///         after 4s, and a long-message toast. Each test pushes a toast via
///         <see cref="MainViewModel.AddToast"/> and captures a screenshot.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class ToastTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

    /// <summary>
    ///     Info toast: blue accent border, "Info" kind label, message visible.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_Info_BlueAccentAndMessage()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Info: connection established.", ToastKind.Info)));
        await Task.Delay(300).ConfigureAwait(false);

        var hasMsg = await Driver.WaitForTextAsync("Info: connection established.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue();

        var path = await CaptureAsync("toast-info").ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "1 toast notification visible in the bottom-right corner of the window (above the status bar). " +
            "The toast card has a blue accent border on its left edge (3px wide), a small blue info icon, " +
            "the kind label 'Info' (blue, semi-bold), and the body text 'Info: connection established.' " +
            "Toast card has a dark surface background, rounded corners, and a drop shadow.",
            nameof(Toast_Info_BlueAccentAndMessage)).ConfigureAwait(false);
        
    }

    /// <summary>
    ///     Success toast: green accent border.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_Success_GreenAccent()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Success: file saved.", ToastKind.Success)));
        await Task.Delay(300).ConfigureAwait(false);

        var hasMsg = await Driver.WaitForTextAsync("Success: file saved.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue();

        var path = await CaptureAsync("toast-success").ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "1 toast notification in the bottom-right. The toast has a GREEN accent border on its left edge, " +
            "a green checkmark icon, the kind label 'Success' (green, semi-bold), and the body text 'Success: file saved.' " +
            "Drop shadow under the card.",
            nameof(Toast_Success_GreenAccent)).ConfigureAwait(false);
        
    }

    /// <summary>
    ///     Warning toast: peach/orange accent border.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_Warning_PeachAccent()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Warning: rate limit approaching.", ToastKind.Warning)));
        await Task.Delay(300).ConfigureAwait(false);

        var hasMsg = await Driver.WaitForTextAsync("Warning: rate limit approaching.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue();

        var path = await CaptureAsync("toast-warning").ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "1 toast notification in the bottom-right. The toast has a PEACH/ORANGE accent border on its left edge, " +
            "a peach warning icon, the kind label 'Warning' (peach, semi-bold), and the body text " +
            "'Warning: rate limit approaching.' Drop shadow under the card.",
            nameof(Toast_Warning_PeachAccent)).ConfigureAwait(false);
        
    }

    /// <summary>
    ///     Error toast: red accent border.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_Error_RedAccent()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Error: provider returned 503.", ToastKind.Error)));
        await Task.Delay(300).ConfigureAwait(false);

        var hasMsg = await Driver.WaitForTextAsync("Error: provider returned 503.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue();

        var path = await CaptureAsync("toast-error").ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "1 toast notification in the bottom-right. The toast has a RED accent border on its left edge, " +
            "a red error icon, the kind label 'Error' (red, semi-bold), and the body text " +
            "'Error: provider returned 503.' Drop shadow under the card.",
            nameof(Toast_Error_RedAccent)).ConfigureAwait(false);
        
    }

    /// <summary>
    ///     Multiple toasts: pushed in rapid succession, they stack vertically
    ///     in the bottom-right with consistent spacing.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_Multiple_StackedVertically()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.AddToast(new ToastNotification("First toast body", ToastKind.Info));
            Vm.AddToast(new ToastNotification("Second toast body", ToastKind.Success));
            Vm.AddToast(new ToastNotification("Third toast body", ToastKind.Warning));
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasFirst = await Driver.WaitForTextAsync("First toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasSecond = await Driver.WaitForTextAsync("Second toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasThird = await Driver.WaitForTextAsync("Third toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasFirst && hasSecond && hasThird).IsTrue();

        var path = await CaptureAsync("toast-multiple-stacked").ConfigureAwait(false);

        // Wait for auto-dismiss so the next test starts clean.
        await Task.Delay(5_000).ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "3 toast notifications stacked VERTICALLY in the bottom-right corner of the window. " +
            "Each toast has a different accent colour: top = Info (blue), middle = Success (green), bottom = Warning (peach). " +
            "The toast bodies read 'First toast body', 'Second toast body', 'Third toast body' respectively. " +
            "Consistent 8px vertical spacing between them. Each has its own drop shadow.",
            nameof(Toast_Multiple_StackedVertically)).ConfigureAwait(false);
        
    }

    /// <summary>
    ///     Auto-dismiss: a toast disappears 4s after being pushed.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_AutoDismiss_After4Seconds()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Auto-dismiss test", ToastKind.Info)));
        await Task.Delay(300).ConfigureAwait(false);

        var hasToastBefore = await Driver.WaitForTextAsync("Auto-dismiss test", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasToastBefore).IsTrue();

        // Wait for auto-dismiss (4s) + buffer.
        await Task.Delay(5_000).ConfigureAwait(false);

        var stillThere = Driver.GetAllVisibleText().Contains("Auto-dismiss test", StringComparison.Ordinal);
        await Assert.That(stillThere).IsFalse();

        var path = await CaptureAsync("toast-auto-dismissed").ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Main window 5 seconds after a toast was pushed. The toast 'Auto-dismiss test' is NO LONGER visible — " +
            "it auto-dismissed after 4 seconds. The bottom-right corner is empty (no toast cards). " +
            "The chat empty-state placeholder is still visible in the center.",
            nameof(Toast_AutoDismiss_After4Seconds)).ConfigureAwait(false);
        
    }

    /// <summary>
    ///     Long-message toast: the toast wraps the message within its
    ///     MaxWidth=380 boundary; the card grows vertically.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toast_LongMessage_WrapsWithinMaxWidth()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification(
            "This is a deliberately long toast message that should wrap across multiple lines because the toast card has a MaxWidth=380 constraint. " +
            "The wrapping should preserve word boundaries and not overflow the card's horizontal bounds.",
            ToastKind.Info)));
        await Task.Delay(300).ConfigureAwait(false);

        var hasMsg = await Driver.WaitForTextAsync("deliberately long toast", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue();

        var path = await CaptureAsync("toast-long-message").ConfigureAwait(false);

        // Wait for auto-dismiss.
        await Task.Delay(5_000).ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "1 toast notification in the bottom-right containing a LONG multi-line message. The toast card is at " +
            "its MaxWidth (~380px) and the message wraps across multiple lines (3-5 lines) without overflowing " +
            "the card's horizontal bounds. The card grows vertically to fit the wrapped text. Blue accent border " +
            "on the left, 'Info' label, drop shadow.",
            nameof(Toast_LongMessage_WrapsWithinMaxWidth)).ConfigureAwait(false);
        
    }
}
