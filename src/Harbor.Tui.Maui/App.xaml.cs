using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Application = Microsoft.Maui.Controls.Application;
using Window = Microsoft.Maui.Controls.Window;

namespace Harbor.Tui.Maui;

/// <summary>
///     App subclass required by MAUI. The renderer's <see cref="MauiProgram" />
///     wires it via <c>UseMauiApp&lt;App&gt;()</c>; <see cref="CreateWindow" />
///     constructs the <see cref="MainPage" /> and notifies the bridge.
/// </summary>
public partial class App : Application
{
    /// <summary>Construct the MAUI App.</summary>
    public App() { InitializeComponent(); }

    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var services = Handler?.MauiContext?.Services
            ?? throw new InvalidOperationException("MAUI services not available");
        var store = services.GetRequiredService<UiStore>();
        var effects = services.GetRequiredService<TuiEffectHost>();
        var lines = services.GetRequiredService<System.Collections.ObjectModel.ObservableCollection<ChatLineViewModel>>();
        var logger = services.GetRequiredService<ILogger>();
        var bridge = services.GetRequiredService<MauiBridge>();

        var page = new MainPage(store, effects, lines, logger);
        bridge.OnReady(page);
        return new Window(page);
    }
}
