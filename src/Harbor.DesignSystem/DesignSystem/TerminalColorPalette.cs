namespace Harbor.DesignSystem;

/// <summary>
/// Terminal-specific design tokens matching the HTML design-system report.
/// These are the exact colors specified for CellForge and TUI rendering.
///
/// Token reads resolve against the active <see cref="HarborTheme" />. The
/// active theme and the installed <see cref="ThemeOverrideSet" /> are published
/// together as one immutable snapshot, so a reader always sees a coherent pair
/// (<see cref="EffectiveTheme" />). <see cref="Apply" /> swaps that snapshot with
/// an interlocked compare-exchange and fires <see cref="ThemeChanged" /> only
/// after the new state is visible, so derived palettes (ChatPalette and friends)
/// re-project against what was just published. Default theme:
/// <see cref="HarborTheme.HarborDark" />.
/// </summary>
/// <remarks>
/// <para>
///     <b>Why one snapshot (#2625).</b> The theme and the overrides used to be
///     two independent volatile fields. A reader of <see cref="EffectiveTheme" />
///     took one of each, so a concurrent <see cref="SetOverrides" /> could pair
///     an old theme with a new override set (or the reverse). Both halves now
///     live in a single immutable <c>ThemeSnapshot</c> behind one reference;
///     readers take that reference once, writers replace it atomically.
/// </para>
/// <para>
///     <b>Event contract.</b> <see cref="ThemeChanged" /> is raised synchronously
///     on the calling thread (including the theme watchers' timer threads)
///     after the snapshot has been published. The invocation list is copied once
///     before it is invoked, so a concurrent unsubscribe cannot break the call,
///     and no lock is held while handlers run (a handler may re-enter
///     <see cref="Apply" /> / <see cref="SetOverrides" />). Handlers must not
///     assume a UI dispatcher: this assembly has zero ProjectReferences by
///     design, so it cannot marshal. Derived palettes stay dispatcher-agnostic
///     by re-projecting into their own volatile fields, which are safe to
///     publish from any thread.
/// </para>
/// </remarks>
public static class TerminalColorPalette
{
    /// <summary>
    /// The two pieces of global theme state, frozen into one value. All readers
    /// take a single reference, which is the barrier for the theme/overrides pair.
    /// </summary>
    private sealed record ThemeSnapshot(HarborTheme Theme, ThemeOverrideSet? Overrides);

    // Not `volatile` on purpose: every access goes through Volatile.Read or
    // Interlocked.CompareExchange, which gives acquire/release without the
    // CS0420 warning a `ref volatile` argument would produce.
    private static ThemeSnapshot _snapshot = new(HarborTheme.HarborDark, null);

    /// <summary>The active theme instance (unpatched).</summary>
    public static HarborTheme Current => Volatile.Read(ref _snapshot).Theme;

    /// <summary>Fired after <see cref="Apply" /> or <see cref="SetOverrides" /> changed the effective catalog.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>Installs <paramref name="theme" /> and raises <see cref="ThemeChanged" />. Re-applying the same instance is a no-op.</summary>
    public static void Apply(HarborTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        // Preserve the installed overrides; only the theme half changes. The
        // compare-exchange loop keeps a concurrent SetOverrides from being lost.
        while (true)
        {
            ThemeSnapshot current = Volatile.Read(ref _snapshot);
            if (ReferenceEquals(current.Theme, theme))
            {
                return;
            }

            var updated = new ThemeSnapshot(theme, current.Overrides);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
            {
                break;
            }
        }

        RaiseThemeChanged();
    }

    /// <summary>
    /// Installs per-component theme overrides (null clears) and raises
    /// <see cref="ThemeChanged" /> so scoped consumers re-project.
    /// </summary>
    public static void SetOverrides(ThemeOverrideSet? overrides)
    {
        // Same shape as Apply: replace only the overrides half, atomically,
        // and never lose a concurrent Apply. Like before, this always fires
        // the event (even when clearing an already-empty set).
        while (true)
        {
            ThemeSnapshot current = Volatile.Read(ref _snapshot);
            var updated = new ThemeSnapshot(current.Theme, overrides);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
            {
                break;
            }
        }

        RaiseThemeChanged();
    }

    /// <summary>
    /// Effective theme for a component scope: the scope's patch merged over
    /// <see cref="Current" />; no scope / no patch → Current unchanged.
    /// The snapshot is read once so the theme and overrides cannot tear.
    /// </summary>
    public static HarborTheme EffectiveTheme(string? scope)
    {
        ThemeSnapshot snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Overrides?.Merge(scope, snapshot.Theme) ?? snapshot.Theme;
    }

    /// <summary>
    /// Publishes the change event. The snapshot write above is a full fence, so
    /// every handler that reads <see cref="Current" /> / <see cref="EffectiveTheme" />
    /// sees the state that triggered it. The delegate is copied once (a
    /// concurrent add/remove operates on the compiler-generated backing field
    /// and cannot null out this local), and handlers run outside any lock.
    /// </summary>
    private static void RaiseThemeChanged()
    {
        EventHandler? handler = Volatile.Read(ref ThemeChanged);
        handler?.Invoke(null, EventArgs.Empty);
    }

    // ── Accent tokens ──────────────────────────────────────────────────────
    public static RgbColor Accent => Current.Accent;
    public static RgbColor Success => Current.Success;
    public static RgbColor Warning => Current.Warning;
    public static RgbColor Error => Current.Error;
    public static RgbColor Tool => Current.Tool;
    public static RgbColor System => Current.System;
    public static RgbColor User => Current.User;

    // ── Surface tokens ─────────────────────────────────────────────────────
    public static RgbColor Background => Current.Background;
    public static RgbColor Panel => Current.Panel;
    public static RgbColor Surface => Current.Surface;
    public static RgbColor Surface2 => Current.Surface2;
    public static RgbColor Border => Current.Border;
    public static RgbColor Muted => Current.Muted;

    public static RgbColor Text => Current.Text;
    public static RgbColor TextDim => Current.Muted;
}
