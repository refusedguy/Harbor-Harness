using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Harbor.App.Avalonia.Configuration;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Switches between the Catppuccin-Mocha (dark) and Catppuccin-Latte (light)
///     themes by swapping the merged resource dictionaries on the application.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly ILogger<ThemeService> _logger;
    private Application? _app;

    /// <summary>Construct a <see cref="ThemeService" />.</summary>
    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    /// <summary>The owning <see cref="Application" />. Set by <see cref="App.OnFrameworkInitializationCompleted" />.</summary>
    public Application Application
    {
        get => _app ?? throw new InvalidOperationException("ThemeService.Application is not set yet.");
        set => _app = value;
    }

    /// <summary>True when dark theme is active. Defaults to true.</summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>Current theme name.</summary>
    public string Current => IsDark ? "dark" : "light";

    /// <summary>
    ///     Swap the merged resource dictionaries so the dark (Mocha) palette
    ///     is the only one loaded. Also sets <see cref="Application.RequestedThemeVariant" />
    ///     so FluentTheme + theme-aware resources pick up the dark variant.
    /// </summary>
    private void ApplyResourceDictionary(bool dark)
    {
        if (_app is null) return;
        var merged = _app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceInclude inc
                && inc.Source?.ToString() is string src
                && (src.Contains("Themes/Dark.axaml", StringComparison.Ordinal)
                    || src.Contains("Themes/Light.axaml", StringComparison.Ordinal)
                    || src.Contains("Themes/Harbor", StringComparison.Ordinal)))
            {
                merged.RemoveAt(i);
            }
        }
        string themePath = dark
            ? "avares://Harbor.App.Avalonia/Themes/Dark.axaml"
            : "avares://Harbor.App.Avalonia/Themes/Light.axaml";
        var newTheme = new ResourceInclude(new Uri("avares://Harbor.App.Avalonia/", UriKind.Absolute))
        {
            Source = new Uri(themePath, UriKind.Absolute)
        };
        merged.Add(newTheme);
        string harborPath = dark
            ? "avares://Harbor.App.Avalonia/Themes/HarborDark.axaml"
            : "avares://Harbor.App.Avalonia/Themes/HarborLight.axaml";
        var harborTheme = new ResourceInclude(new Uri("avares://Harbor.App.Avalonia/", UriKind.Absolute))
        {
            Source = new Uri(harborPath, UriKind.Absolute)
        };
        merged.Add(harborTheme);
        _logger.LogDebug("Theme resource dictionary swapped to {Theme}", dark ? "Dark" : "Light");
    }

    /// <summary>
    ///     Apply the theme configured in <paramref name="config" />. Called once
    ///     at startup from <c>App.OnFrameworkInitializationCompleted</c> after
    ///     <see cref="Application" /> is set. The mapping is:
    ///     <list type="bullet">
    ///         <item><c>"dark"</c> → <see cref="ApplyDark" />.</item>
    ///         <item><c>"light"</c> → <see cref="ApplyLight" />.</item>
    ///         <item><c>"system"</c> (or anything else) → leave the default (dark).</item>
    ///     </list>
    /// </summary>
    /// <param name="config">The Avalonia configuration whose <see cref="AvaloniaConfig.Theme" /> field is read.</param>
    /// <param name="themeMode">Optional explicit theme mode override from CLI arg (--theme). When set, overrides config.Theme.</param>
    public void ApplyFromConfig(AvaloniaConfig config, string? themeMode = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? effectiveTheme = themeMode ?? config.Theme;
        if (string.IsNullOrWhiteSpace(effectiveTheme))
        {
            effectiveTheme = "dark";
        }
        string theme = effectiveTheme.ToLowerInvariant();
        switch (theme)
        {
            case "dark":
                ApplyDark();
                break;
            case "light":
                ApplyLight();
                break;
            case "system":
                ApplyDark();
                break;
            default:
                ApplyDark();
                _logger.LogInformation("Unknown theme '{Theme}', falling back to dark.", theme);
                break;
        }
    }

    /// <summary>
    ///     Apply a theme by name: <c>"dark"</c>, <c>"light"</c>, or
    ///     <c>"system"</c> (which resolves to dark for now — the actual
    ///     OS-level light/dark detection is a separate task). Unknown
    ///     values fall back to dark. Used by SettingsViewModel when the
    ///     user picks a theme from the dropdown.
    /// </summary>
    /// <param name="theme">Theme name (case-insensitive).</param>
    public void Apply(string theme)
    {
        string t = (theme ?? "system").ToLowerInvariant();
        switch (t)
        {
            case "light":
                ApplyLight();
                break;
            case "dark":
                ApplyDark();
                break;
            default:
                // "system" or unknown → leave the default dark theme active.
                _logger.LogInformation("Theme '{Theme}' requested — leaving default dark theme active", theme);
                ApplyDark();
                break;
        }
    }

    /// <summary>Apply the dark (Mocha) theme.</summary>
    public void ApplyDark()
    {
        if (_app is null) return;
        ApplyResourceDictionary(dark: true);
        _app.RequestedThemeVariant = ThemeVariant.Dark;
        IsDark = true;
        _logger.LogInformation("Theme switched to Mocha (dark)");
    }

    /// <summary>Apply the light (Latte) theme.</summary>
    public void ApplyLight()
    {
        if (_app is null) return;
        ApplyResourceDictionary(dark: false);
        _app.RequestedThemeVariant = ThemeVariant.Light;
        IsDark = false;
        _logger.LogInformation("Theme switched to Latte (light)");
    }

    /// <summary>Toggle between dark and light themes.</summary>
    public void Toggle()
    {
        if (IsDark) ApplyLight();
        else ApplyDark();
    }
}
