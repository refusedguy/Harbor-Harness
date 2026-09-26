using System.Diagnostics.CodeAnalysis;
using CSharpFunctionalExtensions;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Harbor.App.Avalonia.Configuration;
using Harbor.Ui.Framework.Services;
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
    private global::Avalonia.Application? _app;

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    public global::Avalonia.Application Application
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

    public event EventHandler<string>? ThemeJsonApplied;

    public Result<string> LoadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Result.Failure<string>($"theme file not found: {path}");
            return Result.Success(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"theme load failed: {ex.Message}");
        }
    }

    public Result ApplyJson(string json)
    {
        // Avalonia HDS themes are XAML-based; JSON themes are handled by the terminal renderer.
        // For the desktop app we just raise the event so watchers can react and report success
        // if the JSON is non-empty. Real JSON theme parsing lives in Harbor.Tui.CellForge.JsonThemeLoader.
        if (string.IsNullOrWhiteSpace(json))
            return Result.Failure("theme json is empty");
        ThemeJsonApplied?.Invoke(this, json);
        _logger.LogInformation("Theme JSON applied ({Length} chars)", json.Length);
        return Result.Success();
    }

    public IDisposable Watch(string path)
    {
        // Minimal file watcher that re-applies JSON on change. Mirrors the terminal JsonThemeLoader.Watch
        // but delegates HDS handling to ApplyJson.
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            var file = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                return new NoopDisposable();
            var fsw = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            fsw.Changed += (_, _) =>
            {
                var res = LoadJson(path);
                if (res.IsSuccess)
                    ApplyJson(res.Value);
            };
            return fsw;
        }
        catch
        {
            return new NoopDisposable();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

}

