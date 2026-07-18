using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Onboarding wizard window. Shown on first launch (or whenever the user
///     re-runs onboarding from Settings) before the main window. Modal in
///     effect — <c>App.axaml.cs</c> does not show <see cref="MainWindow"/>
///     until this window closes via <see cref="OnboardingViewModel.Completed"/>.
/// </summary>
public partial class OnboardingWindow : Window
{
    /// <summary>Construct the onboarding window. Avalonia's generated InitializeComponent runs first.</summary>
    public OnboardingWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            // Dispose the VM (cancels its CTS) so background work doesn't
            // outlive the wizard. Safe to call even if the user never started
            // any async work — Cancel on an unused CTS is a no-op.
            (DataContext as IDisposable)?.Dispose();
        };
    }

    /// <summary>
    ///     Wire the <see cref="OnboardingViewModel.Completed"/> event so the
    ///     window closes itself when the wizard finishes. Called by
    ///     <c>App.axaml.cs</c> after constructing the view-model.
    /// </summary>
    /// <param name="viewModel">The onboarding view-model bound to this window.</param>
    public void Bind(OnboardingViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Completed += (_, _) =>
        {
            // Close on the next loop pass so the await chain in FinishAsync /
            // Skip can finish unwinding. Closing synchronously inside the
            // event handler can leave the await continuation orphaned. The
            // result distinguishes finish (Completed) from skip (Skipped) so
            // App.axaml.cs knows whether to persist OnboardingCompleted=true.
            var result = viewModel.IsCompleted
                ? OnboardingResult.Completed
                : OnboardingResult.Skipped;
            Dispatcher.UIThread.Post(() => Close(result));
        };
    }

    /// <summary>Result returned by <see cref="ShowDialog{T}"/> — distinguishes finish vs. skip vs. window-X.</summary>
    public enum OnboardingResult
    {
        /// <summary>User closed the window via the X button without finishing — treat as cancel. (Default enum value, returned by ShowDialog when Close(result) is never called.)</summary>
        Cancelled,

        /// <summary>User clicked Skip — open the main window with defaults. OnboardingCompleted stays false so the wizard reappears next launch.</summary>
        Skipped,

        /// <summary>User completed the wizard and saved their config. OnboardingCompleted is now true.</summary>
        Completed,
    }

    /// <summary>Refresh CanAdvance + SelectedProvider when a step-2 checkbox toggles.</summary>
    private void Provider_Checkbox_Changed(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm)
        {
            vm.RefreshSelectedProviderCommand.Execute(null);
        }
    }

    // ── Custom Windows title bar handlers (Task U1) ──
    // See MainWindow.axaml.cs for the full rationale — same pattern, just
    // without a Maximize button because this window is CanResize=False.

    /// <summary>Begin a window drag when the user presses the mouse on the custom title bar.</summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // No-op on a fixed-size window, but swallow the click so the
            // drag below doesn't start on the second press of a double-click.
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }

    /// <summary>Minimize button — collapses to the taskbar.</summary>
    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    /// <summary>Close button — triggers the standard Window.Close path.</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
