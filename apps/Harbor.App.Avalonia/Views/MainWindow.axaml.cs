using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Shell;
using Harbor.App.Avalonia.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Main window code-behind. Hosts the shell layout and forwards every
///     global input event (title-bar drag, caption buttons, keyboard
///     shortcuts) to dedicated services resolved from DI
///     (<see cref="WindowChromeService" /> and
///     <see cref="KeyboardShortcutService" />). The code-behind itself only
///     wires up services — no behaviour lives here. Disposes the bound
///     <see cref="MainViewModel" /> on close so the UiStore subscription
///     and toast continuations are torn down cleanly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Experimental shell-mode switch (Task F2):</b> when
///         <see cref="App.IsOrcaShell" /> is <c>true</c> the classic
///         Catppuccin-Mocha XAML body is replaced with an
///         <see cref="OrcaShellView" />; classic mode is unaffected.
///     </para>
/// </remarks>
public partial class MainWindow : Window
{

    /// <summary>Resolve <see cref="WindowChromeService" /> from DI (cached on first use).</summary>
    private WindowChromeService? _chrome;

    /// <summary>Resolve <see cref="KeyboardShortcutService" /> from DI (cached on first use).</summary>
    private KeyboardShortcutService? _keyboard;
    /// <summary>Construct the main window. Avalonia's generated InitializeComponent runs first.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Apply persisted window geometry from AvaloniaConfig (~/.harbor/avalonia.json).
        // The XAML defaults (Width=1280, Height=800) are the fallback when no config
        // is available yet (design-time previewer, or first-run before the config
        // file is written).
        ApplyConfiguredGeometry();

        // ── Experimental Orca shell swap (Task F2) ──────────────────────────
        if (App.IsOrcaShell)
        {
            this.Content = new OrcaShellView();
        }

        // Defensive: ensure Chat is the active view and NO modal is open when the
        // shell first appears. Fires after every binding has been applied so it's
        // the LAST word on the initial UI state.
        this.Loaded += (_, _) =>
        {
            if (this.DataContext is MainViewModel vm)
            {
                vm.ActiveView = "chat";
                vm.IsDiffOpen = false;
                vm.IsTokenUsageOpen = false;
                vm.IsCommandPaletteOpen = false;
                vm.IsSettingsOpen = false;
                vm.IsProviderBrowserOpen = false;
                vm.IsModelPickerOpen = false;
            }
            else if (this.DataContext is OrcaShellViewModel orcaVm)
            {
                // Orca shell initial state: chat mode, no right panel.
                orcaVm.SwitchModeCommand.Execute("Chat");
            }
        };

        // Dispose the MainViewModel when the window closes — this unsubscribes
        // the UiStore → dispatcher bridge and prevents background toast
        // continuations from racing the window teardown.
        this.Closing += (_, _) =>
        {
            if (this.DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private WindowChromeService Chrome =>
        _chrome ??= App.Services?.GetService<WindowChromeService>() ?? new WindowChromeService();

    private KeyboardShortcutService Keyboard =>
        _keyboard ??= App.Services?.GetService<KeyboardShortcutService>() ?? new KeyboardShortcutService();

    private MainViewModel? Vm => this.DataContext switch
    {
        MainViewModel vm => vm,
        OrcaShellViewModel orca => orca.Main,
        _ => null
    };

    /// <summary>
    ///     Read <see cref="AvaloniaConfig.WindowWidth" /> / <see cref="AvaloniaConfig.WindowHeight" />
    ///     / <see cref="AvaloniaConfig.WindowMaximized" /> from DI and apply them to this
    ///     window. No-op when <see cref="App.Services" /> is null (design-time).
    /// </summary>
    private void ApplyConfiguredGeometry()
    {
        var services = App.Services;
        if (services is null) return;
        var config = services.GetService<AvaloniaConfig>();
        if (config is null) return;

        if (config.WindowWidth > 0)
            this.Width = config.WindowWidth;
        if (config.WindowHeight > 0)
            this.Height = config.WindowHeight;
        if (config.WindowMaximized)
        {
            this.WindowState = WindowState.Maximized;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.HandleKeyDown(Vm, e))
        {
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void Quit_Click(object? sender, RoutedEventArgs e) => Chrome.Close(this);

    private void ViewChat_Click(object? sender, RoutedEventArgs e) =>
        Vm?.SwitchViewCommand.Execute("chat");

    private void ViewCode_Click(object? sender, RoutedEventArgs e) =>
        Vm?.SwitchViewCommand.Execute("code");

    // ── Custom Windows title bar handlers (ExtendClientAreaToDecorationsHint) ──
    //
    // The XAML draws its own caption buttons (Button.WindowButton styled in
    // Themes/AppStyles.axaml) and forwards their click events here. Each
    // handler is a one-liner that delegates to WindowChromeService so no
    // behaviour lives in the code-behind.

    /// <summary>
    ///     Title-bar drag / double-click maximize — delegates to
    ///     <see cref="WindowChromeService.HandleTitleBarPointerPressed" />.
    /// </summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Chrome.HandleTitleBarPointerPressed(this, e);

    /// <summary>Minimize button — delegates to <see cref="WindowChromeService.Minimize" />.</summary>
    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        Chrome.Minimize(this);

    /// <summary>Maximize / restore button — delegates to <see cref="WindowChromeService.MaximizeOrRestore" />.</summary>
    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        Chrome.MaximizeOrRestore(this);

    /// <summary>Close button — delegates to <see cref="WindowChromeService.Close" />.</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) =>
        Chrome.Close(this);

    /// <summary>
    ///     Close the provider/model picker flyout when the user clicks the
    ///     dark scrim outside the picker card.
    /// </summary>
    private void PickerBackdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && Vm is { } vm)
        {
            vm.IsModelPickerOpen = false;
        }
    }

    /// <summary>Close button on the picker card header.</summary>
    private void PickerClose_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.IsModelPickerOpen = false;
        }
    }
}
