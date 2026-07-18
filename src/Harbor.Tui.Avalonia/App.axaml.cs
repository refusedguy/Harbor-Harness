using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Harbor.Tui.Avalonia;

/// <summary>
///     Avalonia <c>Application</c> subclass. The renderer builds the app via
///     <c>AppBuilder.Configure&lt;App&gt;()</c> and starts the classic desktop
///     lifetime on a dedicated UI thread; this class wires up the Fluent theme
///     and finalizes the main window.
/// </summary>
public class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // MainWindow is created in AppBuilder.AfterSetup so the renderer can
            // inject UiStore + effect host. This handler only finalizes the lifetime.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
