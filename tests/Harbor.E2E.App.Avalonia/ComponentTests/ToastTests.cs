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
[NotInParallel("e2e-framework")]
public sealed class ToastTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Toast").ConfigureAwait(false);

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

        var hasMsg = await Driver.WaitForTextAsync("Info: connection established.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync("toast-info").ConfigureAwait(false);
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

        var hasMsg = await Driver.WaitForTextAsync("Success: file saved.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync("toast-success").ConfigureAwait(false);
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

        var hasMsg = await Driver.WaitForTextAsync("Warning: rate limit approaching.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync("toast-warning").ConfigureAwait(false);
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

        var hasMsg = await Driver.WaitForTextAsync("Error: provider returned 503.", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMsg).IsTrue().Because(Driver.LastWaitDiagnostic);

        var path = await CaptureAsync("toast-error").ConfigureAwait(false);
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

        // Tear the toast down deterministically instead of waiting out the
        // product's 4s auto-dismiss timer. The timer is still exercised — and
        // asserted — by Toast_AutoDismiss_After4Seconds below; what this
        // test needs is "the next test starts with no toast on screen", and
        // dropping the collection is what actually delivers that. The wait
        // that stood here was four seconds of a fully serialised project
        // (#1790) spent on a timer whose firing this test never asserted.
        UI(() => Vm.Toasts.Clear());
        await Driver.ShowMainWindowAsync().ConfigureAwait(false);
        await Driver.WaitForTextAbsentAsync("First toast body", TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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

        // Wait for auto-dismiss (4s) — poll until the toast is actually gone.
        await Driver.WaitForTextAbsentAsync("Auto-dismiss test", TimeSpan.FromSeconds(7)).ConfigureAwait(false);

        var stillThere = Driver.GetAllVisibleText().Contains("Auto-dismiss test", StringComparison.Ordinal);
        await Assert.That(stillThere).IsFalse();

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

        // Tear the toast down deterministically — same reason as
        // Toast_Multiple_StackedVertically: the auto-dismiss timer has its
        // own asserting test, and this one only needs the screen clean for
        // the next test in a class that runs one test at a time.
        UI(() => Vm.Toasts.Clear());
        await Driver.ShowMainWindowAsync().ConfigureAwait(false);
        await Driver.WaitForTextAbsentAsync("deliberately long toast", TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
}
