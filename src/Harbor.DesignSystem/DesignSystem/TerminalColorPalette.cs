namespace Harbor.DesignSystem;

/// <summary>
/// Terminal-specific design tokens matching the HTML design-system report.
/// These are the exact colors specified for ConsoleEx and TUI rendering.
///
/// Token reads resolve against the active <see cref="HarborTheme" /> —
/// <see cref="Apply" /> swaps it atomically (volatile reference) and fires
/// <see cref="ThemeChanged" /> so derived palettes (ChatPalette and friends)
/// can re-project their styles. Default theme: <see cref="HarborTheme.HarborDark" />.
/// </summary>
public static class TerminalColorPalette
{
    private static volatile HarborTheme _current = HarborTheme.HarborDark;

    /// <summary>The active theme instance.</summary>
    public static HarborTheme Current => _current;

    /// <summary>Fired after <see cref="Apply" /> installed a new theme.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>Installs <paramref name="theme" /> and raises <see cref="ThemeChanged" />. Re-applying the same instance is a no-op.</summary>
    public static void Apply(HarborTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (ReferenceEquals(_current, theme))
        {
            return;
        }

        _current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    // ── Accent tokens ──────────────────────────────────────────────────────
    public static RgbColor Accent => _current.Accent;
    public static RgbColor Success => _current.Success;
    public static RgbColor Warning => _current.Warning;
    public static RgbColor Error => _current.Error;
    public static RgbColor Tool => _current.Tool;
    public static RgbColor System => _current.System;
    public static RgbColor User => _current.User;

    // ── Surface tokens ─────────────────────────────────────────────────────
    public static RgbColor Background => _current.Background;
    public static RgbColor Panel => _current.Panel;
    public static RgbColor Surface => _current.Surface;
    public static RgbColor Surface2 => _current.Surface2;
    public static RgbColor Border => _current.Border;
    public static RgbColor Muted => _current.Muted;

    public static RgbColor Text => _current.Text;
    public static RgbColor TextDim => _current.Muted;
}
