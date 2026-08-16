using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Harbor.App.Avalonia.Configuration;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Switches between HDS theme palettes by replacing the HDS merged
///     resource dictionary. Also manages dark/light mode for the base
///     app chrome.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string HdsThemePrefix = "avares://Harbor.App.Avalonia/Themes/Hds/";
    private const string DefaultHdsTheme = "CatppuccinMocha.axaml";

    private readonly ILogger<ThemeService> _logger;
    private Application? _app;

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    public Application Application
    {
        get => _app ?? throw new InvalidOperationException("ThemeService.Application is not set yet.");
        set => _app = value;
    }

    public bool IsDark { get; private set; } = true;
    public string Current => IsDark ? "dark" : "light";

    /// <summary>
    ///     Apply an HDS theme by name (e.g. "CatppuccinMocha", "Vapor", "Mono").
    ///     Replaces the HDS palette dictionary in the application's merged
    ///     dictionaries so AppBackground, fonts, and all semantic brushes
    ///     update instantly.
    /// </summary>
    /// <param name="theme">Theme name without extension (case-insensitive).</param>
    public void ApplyHds(string theme)
    {
        if (_app is null) return;
        string normalized = (theme ?? DefaultHdsTheme.Replace(".axaml", "")).Trim();

        var merged = _app.Resources.MergedDictionaries;
        if (merged.Count == 0) return;

        int hdsSlot = 1;
        if (hdsSlot >= merged.Count)
        {
            _logger.LogWarning("HDS theme slot not found in MergedDictionaries — could not switch to {Theme}", normalized);
            return;
        }

        string source = $"{HdsThemePrefix}{normalized}.axaml";
        merged[hdsSlot] = new ResourceInclude(new Uri("avares://Harbor.App.Avalonia/", UriKind.Absolute))
        {
            Source = new Uri(source, UriKind.Absolute)
        };
        _logger.LogInformation("HDS theme switched to {Theme}", normalized);
    }

    /// <summary>
    ///     Apply the theme configured in <paramref name="config" />. Called once
    ///     at startup from App.OnFrameworkInitializationCompleted after
    ///     Application is set.
    /// </summary>
    public void ApplyFromConfig(AvaloniaConfig config, string? themeMode = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? effectiveTheme = themeMode ?? config.Theme;
        if (string.IsNullOrWhiteSpace(effectiveTheme))
        {
            effectiveTheme = "dark";
        }
        Apply(effectiveTheme);
    }

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
                ApplyDark();
                _logger.LogInformation("Theme '{Theme}' requested — leaving default dark theme active", theme);
                break;
        }
    }

    public void ApplyDark()
    {
        if (_app is null) return;
        ApplyHds("CatppuccinMocha");
        _app.RequestedThemeVariant = ThemeVariant.Dark;
        IsDark = true;
        _logger.LogInformation("Theme switched to dark");
    }

    public void ApplyLight()
    {
        if (_app is null) return;
        ApplyHds("Lumen");
        _app.RequestedThemeVariant = ThemeVariant.Light;
        IsDark = false;
        _logger.LogInformation("Theme switched to light");
    }

    public void Toggle()
    {
        if (IsDark) ApplyLight();
        else ApplyDark();
    }

    public void SetThemeVariant(bool isDark)
    {
        if (_app is null) return;
        _app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        IsDark = isDark;
        _logger.LogInformation("Theme variant set to {Variant}", isDark ? "dark" : "light");
    }

}

