using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
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
///     <para>
///         <b>The auto-dismiss is observed on a virtual clock.</b> The shell
///         reads the four-second delay from an injected
///         <see cref="TimeProvider"/>, so these tests step
///         <see cref="ToastClock"/> instead of waiting four real seconds.
///         The four-second contract itself is still asserted, against
///         <see cref="MainViewModel.ToastAutoDismissDelay"/>.
///     </para>
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class ToastTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Toast").ConfigureAwait(false);

    /// <summary>
    ///     One toast per kind: info (blue), success (green), warning (peach),
    ///     error (red) — the accent border, the kind label and the message are
    ///     all visible, and each kind gets its own golden frame.
    /// </summary>
    // #1770: the four kind-copies differed only in the kind, the message and
    // the capture name — the asserts never changed. All three are the table
    // now, and every row keeps its original test name as the display name.
    [Test]
    [Category("E2E")]
    [Category("Component")]
    [Arguments(ToastKind.Info, "Info: connection established.", "toast-info", DisplayName = "Toast_Info_BlueAccentAndMessage")]
    [Arguments(ToastKind.Success, "Success: file saved.", "toast-success", DisplayName = "Toast_Success_GreenAccent")]
    [Arguments(ToastKind.Warning, "Warning: rate limit approaching.", "toast-warning", DisplayName = "Toast_Warning_PeachAccent")]
    [Arguments(ToastKind.Error, "Error: provider returned 503.", "toast-error", DisplayName = "Toast_Error_RedAccent")]
    public async Task Toast_Kind_ShowsItsMessage(ToastKind kind, string message, string captureName)
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification(message, kind)));

        var hasMsg = await Driver.WaitForTextAsync(message, TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync(captureName).ConfigureAwait(false);
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

        var hasFirst = await Driver.WaitForTextAsync("First toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasSecond = await Driver.WaitForTextAsync("Second toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasThird = await Driver.WaitForTextAsync("Third toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasFirst && hasSecond && hasThird).IsTrue();

        var path = await CaptureAsync("toast-multiple-stacked").ConfigureAwait(false);

        // Dismiss the stack so the next test starts clean, on the virtual
        // clock, so this costs no wall-clock time.
        ToastClock.AdvancePastToastDeadline();
        bool dismissed = await Driver.WaitForTextAbsentAsync("First toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(dismissed).IsTrue().Because(Driver.LastWaitDiagnostic);
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

        var hasToastBefore = await Driver.WaitForTextAsync("Auto-dismiss test", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasToastBefore).IsTrue().Because(Driver.LastWaitDiagnostic);

        // The shipped contract, quoted from the product: the shell removes a
        // toast after MainViewModel.ToastAutoDismissDelay. The delay reads the
        // injected clock, so "four seconds later" is an instant the test steps
        // to. The four-second promise is still asserted, against the
        // production constant.
        await Assert.That(MainViewModel.ToastAutoDismissDelay)
            .IsEqualTo(TimeSpan.FromSeconds(4))
            .Because("the shipped toast lifetime is four seconds");

        // One tick short of the deadline the toast is still on screen: the
        // dismissal happens AT the deadline, not before it.
        ToastClock.Advance(MainViewModel.ToastAutoDismissDelay - TimeSpan.FromMilliseconds(1));
        bool stillVisible = Driver.GetAllVisibleText().Contains("Auto-dismiss test", StringComparison.Ordinal);
        await Assert.That(stillVisible).IsTrue()
            .Because("the toast must survive right up to the deadline");

        // At the deadline it is gone. No wall-clock wait, the clock was stepped.
        ToastClock.Advance(TimeSpan.FromMilliseconds(1));
        bool dismissed = await Driver.WaitForTextAbsentAsync("Auto-dismiss test", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(dismissed).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync("toast-auto-dismissed").ConfigureAwait(false);
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

        var hasMsg = await Driver.WaitForTextAsync("deliberately long toast", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync("toast-long-message").ConfigureAwait(false);

        // Dismiss on the virtual clock. The long toast follows the same
        // four-second rule as every other toast.
        ToastClock.AdvancePastToastDeadline();
        bool dismissed = await Driver.WaitForTextAbsentAsync("deliberately long toast", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(dismissed).IsTrue().Because(Driver.LastWaitDiagnostic);
    }
}
