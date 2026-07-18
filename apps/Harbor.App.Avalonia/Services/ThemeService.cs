using Avalonia.Styling;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Harbor.App.Avalonia.Configuration;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Switches between the Catppuccin-Mocha (dark) and Catppuccin-Latte (light)
///     themes by swapping the merged resource dictionaries on the application.
/// </summary>
public sealed class ThemeService
{
    private readonly ILogger<ThemeService> _logger;
    private Application? _app;

    /// <summary>Construct a <see cref="ThemeService"/>.</summary>
    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    /// <summary>The owning <see cref="Application"/>. Set by <see cref="App.OnFrameworkInitializationCompleted"/>.</summary>
    public Application Application
    {
        get => _app ?? throw new InvalidOperationException("ThemeService.Application is not set yet.");
        set => _app = value;
    }

    /// <summary>True when dark theme is active. Defaults to true.</summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>
    ///     Apply the theme configured in <paramref name="config"/>. Called once
    ///     at startup from <c>App.OnFrameworkInitializationCompleted</c> after
    ///     <see cref="Application"/> is set. The mapping is:
    ///     <list type="bullet">
    ///         <item><c>"dark"</c> → <see cref="ApplyDark"/>.</item>
    ///         <item><c>"light"</c> → <see cref="ApplyLight"/>.</item>
    ///         <item><c>"system"</c> (or anything else) → leave the default (dark).</item>
    ///     </list>
    /// </summary>
    /// <param name="config">The Avalonia configuration whose <see cref="AvaloniaConfig.Theme"/> field is read.</param>
    public void ApplyFromConfig(AvaloniaConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var theme = (config.Theme ?? "system").ToLowerInvariant();
        switch (theme)
        {
            case "dark":
                ApplyDark();
                break;
            case "light":
                ApplyLight();
                break;
            default:
                // "system" or unknown — leave the default dark theme active.
                _logger.LogInformation("Theme 'system' requested — leaving default dark theme active.");
                break;
        }
    }

    /// <summary>Apply the dark (Mocha) theme.</summary>
    public void ApplyDark()
    {
        if (_app is null) return;
        _app.RequestedThemeVariant = ThemeVariant.Dark;
        IsDark = true;
        _logger.LogInformation("Theme switched to Mocha (dark)");
    }

    /// <summary>Apply the light (Latte) theme.</summary>
    public void ApplyLight()
    {
        if (_app is null) return;
        _app.RequestedThemeVariant = ThemeVariant.Light;
        IsDark = false;
        _logger.LogInformation("Theme switched to Latte (light)");
    }

    /// <summary>Toggle between dark and light themes.</summary>
    public void Toggle()
    {
        if (IsDark) ApplyLight(); else ApplyDark();
    }
}
