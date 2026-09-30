using System.Diagnostics.CodeAnalysis;
using CSharpFunctionalExtensions;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Themes;
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

        // URI shape is owned by the palette index, so the theme that gets applied
        // and the preview that described it are built from one place (#673).
        merged[hdsSlot] = new ResourceInclude(HdsThemeCatalog.BaseUri)
        {
            Source = HdsThemeCatalog.PaletteUri(normalized)
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

    /// <summary>
    ///     Apply a theme named either by VARIANT (<c>dark</c> / <c>light</c>) or by
    ///     PALETTE (any name <see cref="HdsThemeCatalog.PaletteNames" /> holds).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #583: a name that is neither variant is asked of the palette index
    ///         before it is given up on. This matters because this is the path a
    ///         launch enters — <c>App.OnFrameworkInitializationCompleted</c> calls
    ///         <see cref="ApplyFromConfig" />, which calls this — and the path a saved
    ///         setting is re-applied through on every Settings save. It used to be a
    ///         closed three-arm switch that named <c>Lumen</c> and
    ///         <c>CatppuccinMocha</c> and sent everything else to dark, so four of the
    ///         six shipped palettes could not be applied at all, and a palette chosen
    ///         in the app was undone by the next launch.
    ///     </para>
    ///     <para>
    ///         The variant arms are unchanged, so <c>system</c> and an unrecognised
    ///         name still mean "the default dark theme" exactly as before. What is new
    ///         is that a name the CATALOG recognises is no longer indistinguishable
    ///         from one it does not.
    ///     </para>
    /// </remarks>
    /// <param name="theme">A variant name or a palette name; case-insensitive.</param>
    public void Apply(string theme)
    {
        string t = (theme ?? "system").Trim();

        switch (t.ToLowerInvariant())
        {
            case "light":
                ApplyLight();
                return;
            case "dark":
                ApplyDark();
                return;
        }

        // Not a variant. The only question left is whether it names a palette, and
        // the index is the one place that can answer that without a name of its own.
        if (ApplyPalette(t))
        {
            return;
        }

        ApplyDark();
        _logger.LogInformation("Theme '{Theme}' requested — leaving default dark theme active", theme);
    }

    /// <summary>
    ///     Apply the palette <paramref name="name" />, if the catalog ships one by
    ///     that name, and set the chrome variant the palette itself declares.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the startup half of #583, and it is the same answer the
    ///         Settings screen reaches through <c>IThemeApplier.ApplyHds</c> plus
    ///         <c>SetThemeVariant</c> — two entry points, one source. Neither this
    ///         method nor that one holds a list of palettes, or of which of them are
    ///         dark: both ask <see cref="HdsThemeCatalog" />, and the palette's own
    ///         <c>HdsThemeVariant</c> key is what answers the variant (#673).
    ///     </para>
    ///     <para>
    ///         Returns false rather than throwing for a name the app does not ship,
    ///         because a persisted setting outlives the palette it names: a user whose
    ///         saved <c>Paper</c> is gone should get the default dark theme, not a
    ///         failed launch.
    ///     </para>
    /// </remarks>
    /// <param name="name">Palette name, e.g. <c>Vapor</c>.</param>
    /// <returns>True when a palette was applied; false for an unknown name or no application.</returns>
    public bool ApplyPalette(string name)
    {
        if (_app is null) return false;

        HdsThemePreview? palette = HdsThemeCatalog.Find(name);
        if (palette is null) return false;

        // The catalog's own spelling, not the string that was asked for, so the
        // resource URI is the one HdsThemeCatalogParityTests bound to the folder.
        ApplyHds(palette.Name);

        _app.RequestedThemeVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        IsDark = palette.IsDark;
        _logger.LogInformation("HDS palette {Palette} applied ({Variant})", palette.Name, IsDark ? "dark" : "light");
        return true;
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
        // if the JSON is non-empty. The real JSON theme parser is Harbor.DesignSystem.ThemeJson
        // (reached from the terminal through JsonThemeLoader.Parse, which takes a string, not a
        // path — since #668 reading a theme FILE is IThemeStore, which this app does not use yet).
        if (string.IsNullOrWhiteSpace(json))
            return Result.Failure("theme json is empty");
        ThemeJsonApplied?.Invoke(this, json);
        _logger.LogInformation("Theme JSON applied ({Length} chars)", json.Length);
        return Result.Success();
    }

    public IDisposable Watch(string path) => Watch(path, onError: null);

    public IDisposable Watch(string path, Action<string>? onError)
    {
        // Minimal file watcher that re-applies JSON on change. Mirrors the terminal
        // Harbor.Tui.CellForge.Widgets.ThemeFileWatcher (which polls rather than using
        // FileSystemWatcher) but delegates HDS handling to ApplyJson. Errors are
        // non-fatal; watch resumes on next write.
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
                if (res.IsFailure)
                {
                    onError?.Invoke(res.Error);
                    return;
                }

                var applied = ApplyJson(res.Value);
                if (applied.IsFailure)
                    onError?.Invoke(applied.Error);
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

