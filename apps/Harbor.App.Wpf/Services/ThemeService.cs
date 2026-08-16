using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Wpf.Services;
/// <summary>
///     Manages the active Catppuccin theme (Dark / Light) by swapping the
///     merged dictionary at runtime. Persists the choice to
///     <c>%USERPROFILE%/.harbor/wpf-theme.txt</c> so it survives restarts.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Available themes.</summary>
    public enum Theme
    {
        /// <summary>Dark theme (Catppuccin Mocha).</summary>
        Dark,

        /// <summary>Light theme (Catppuccin Latte).</summary>
        Light
    }

    private const string ThemeFileName = "wpf-theme.txt";
    private readonly ILogger<ThemeService> _logger;

    /// <summary>Construct a <see cref="ThemeService" />.</summary>
    /// <param name="logger">Logger.</param>
    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    /// <summary>The currently applied theme.</summary>
    public Theme Current
    {
        get;
        private set;
    } = Theme.Dark;

    /// <summary>
    ///     Raised whenever the theme changes. Subscribers can use this to
    ///     re-render content that depends on dynamic resource keys.
    /// </summary>
    public event EventHandler<Theme>? ThemeChanged;

    /// <summary>
    ///     Read the persisted theme file (if any) and apply it. Falls back
    ///     to <see cref="Theme.Dark" /> when the file is missing or invalid.
    /// </summary>
    public void ApplyPersistedTheme()
    {
        try
        {
            string path = GetThemeFilePath();
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path).Trim();
                if (Enum.TryParse<Theme>(text, true, out var parsed))
                {
                    ApplyTheme(parsed);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read persisted theme; defaulting to Dark");
        }
        ApplyTheme(Theme.Dark);
    }

    /// <summary>
    ///     Apply the given theme immediately to <see cref="Application.Resources" />.
    /// </summary>
    /// <param name="theme">The theme to apply.</param>
    public void ApplyTheme(Theme theme)
    {
        Current = theme;
        try
        {
            var app = Application.Current;
            if (app is null) return;

            // Toggle semantic resource keys between dark and light values.
            // We do NOT swap the whole merged dictionary (that would lose
            // changes the user made at design time); instead we update the
            // semantic brushes directly so DynamicResource bindings reflow.
            var resources = app.Resources;
            string prefix = theme == Theme.Dark ? "Dark" : "Light";
            string mochaPrefix = "Mocha";
            string lattePrefix = "Latte";
            string palettePrefix = theme == Theme.Dark ? mochaPrefix : lattePrefix;

            // Remap semantic keys.
            RemapSemanticBrushes(resources, palettePrefix);

            // Persist for next run.
            try
            {
                string? dir = Path.GetDirectoryName(GetThemeFilePath());
                if (dir is not null) Directory.CreateDirectory(dir);
                File.WriteAllText(GetThemeFilePath(), theme.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist theme choice");
            }

            ThemeChanged?.Invoke(this, theme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply theme {Theme}", theme);
        }
    }

    /// <summary>
    ///     Toggle between Dark and Light.
    /// </summary>
    public void Toggle() => ApplyTheme(Current == Theme.Dark ? Theme.Light : Theme.Dark);

    private static void RemapSemanticBrushes(ResourceDictionary resources, string palettePrefix)
    {
        // We can't enumerate resources while modifying — snapshot the keys first.
        var semanticMap = new (string Semantic, string Palette)[]
        {
            ("App.Background", "Base"),
            ("App.Surface", "Mantle"),
            ("App.SurfaceAlt", "Surface0"),
            ("App.Border", "Surface1"),
            ("App.Text", "Text"),
            ("App.TextMuted", "Overlay0"),
            ("App.Accent", "Mauve"),
            ("App.AccentSecondary", "Blue"),
            ("App.Success", "Green"),
            ("App.Warning", "Yellow"),
            ("App.Error", "Red"),
            ("App.Info", "Sky")
        };

        foreach ((string semantic, string palette) in semanticMap)
        {
            string paletteKey = $"{palettePrefix}.{palette}Brush";
            if (resources[paletteKey] is SolidColorBrush brush)
            {
                resources[semantic] = brush;
            }
        }
    }

    private static string GetThemeFilePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(home, ".harbor");
        return Path.Combine(harborDir, ThemeFileName);
    }
}
