// StarNagCard.cs — issue #1490: a gentle "star Harbor on GitHub" reminder that
// rides the EXISTING ToastOverlay seat. No new widget primitive, no new overlay
// layer, no new extension axis (#555 freeze): the card is a pure state object
// that feeds the already-shipped toast queue. Persistence is a save delegate
// supplied by the CLI composition root, so this Presentation assembly never
// touches the filesystem.
//
// The card is silent in plain/CI/headless/demo processes. IsEnabled() is the
// single guard; the host calls it before doing any work, and TryShow takes an
// explicit `enabled` override so tests can exercise the silence path without
// touching the process environment.

using Harbor.Abstractions.Env;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Strings;

namespace Harbor.Tui.CellForge.StarNag;

/// <summary>
///     Persisted star-nag counters. Immutable so a paint or a save can never
///     observe a torn snapshot; every mutation returns a new record.
/// </summary>
public sealed record StarNagState
{
    /// <summary>The empty, never-shown state.</summary>
    public static readonly StarNagState Empty = new(0, false, 0);

    /// <summary>Creates a persisted counter snapshot.</summary>
    /// <param name="launches">How many launches have been observed.</param>
    /// <param name="dismissed">True once the user chose "do not show again".</param>
    /// <param name="lastShownAt">The launch number the card was last shown on.</param>
    public StarNagState(int launches, bool dismissed, int lastShownAt)
    {
        Launches = launches < 0 ? 0 : launches;
        Dismissed = dismissed;
        LastShownAt = lastShownAt < 0 ? 0 : lastShownAt;
    }

    public int Launches { get; init; }
    public bool Dismissed { get; init; }
    public int LastShownAt { get; init; }

    /// <summary>Records one more process launch.</summary>
    public StarNagState NoteLaunch() => this with { Launches = Launches + 1 };

    /// <summary>Marks the card as shown on the current launch.</summary>
    public StarNagState MarkShown() => this with { LastShownAt = Launches };

    /// <summary>Persists "do not show again" forever.</summary>
    public StarNagState Dismiss() => this with { Dismissed = true };

    /// <summary>
    ///     True when the card has never been shown, or at least
    ///     <paramref name="interval"/> launches have happened since the last
    ///     show, and the user has not dismissed it for good. A zero
    ///     <see cref="LastShownAt"/> means the first show is still due.
    /// </summary>
    public bool ShouldShow(int interval) =>
        !Dismissed && (LastShownAt == 0 || Launches - LastShownAt >= interval);
}

/// <summary>
///     Gentle GitHub-star reminder (issue #1490). Decides when to show from a
///     persisted launch counter, then enqueues a single toast on the existing
///     <see cref="ToastOverlay"/>. The card has no paint of its own: the toast
///     seat draws it, so there is no new widget, overlay layer or theme token.
/// </summary>
public sealed class StarNagCard
{
    /// <summary>Default "at most once per N launches" cadence.</summary>
    public const int DefaultInterval = 5;

    /// <summary>
    ///     Catalogue key for the toast text (issue #434, one-component extraction
    ///     slice). The trailing keys stand for the two actions the card offers:
    ///     <c>s</c> = star later (postpone one interval), <c>n</c> = never
    ///     (dismiss permanently via <see cref="Dismiss"/>). They are keyboard
    ///     hints, so they stay literal in every locale.
    /// </summary>
    public const string MessageKey = "StarNag_Message";

    private readonly int _interval;
    private readonly Action<StarNagState> _save;
    private readonly IUiStrings _strings;
    private StarNagState _state;

    /// <summary>Creates a card with the given cadence, state and persistence callback.</summary>
    /// <param name="interval">Minimum launches between two shows. Values below 1 fall back to <see cref="DefaultInterval"/>.</param>
    /// <param name="state">Starting state. Null starts from <see cref="StarNagState.Empty"/>.</param>
    /// <param name="save">Persistence callback. Null makes the card in-memory only (tests).</param>
    /// <param name="strings">
    ///     UI string catalogue supplying <see cref="Message"/>. Null uses the
    ///     shared default instance (current locale); pass a
    ///     <see cref="UiStringsCatalog"/> with a pinned locale in tests
    ///     (#1573 substitution seam).
    /// </param>
    public StarNagCard(
        int interval = DefaultInterval,
        StarNagState? state = null,
        Action<StarNagState>? save = null,
        IUiStrings? strings = null)
    {
        _interval = interval < 1 ? DefaultInterval : interval;
        _state = state ?? StarNagState.Empty;
        _save = save ?? (_ => { });
        _strings = strings ?? new UiStringsCatalog();
    }

    /// <summary>
    ///     The localized toast text, resolved from the shared catalogue
    ///     (issue #434). Kept as a lookup rather than a literal so <c>en</c> and
    ///     <c>ru</c> share one source of truth; <see cref="TryShow"/> reads it
    ///     at show time.
    /// </summary>
    public string Message => _strings.Get(MessageKey);

    /// <summary>Current counter snapshot (test seam).</summary>
    public StarNagState State => _state;

    /// <summary>Configured launch interval (test seam).</summary>
    public int Interval => _interval;

    /// <summary>
    ///     Reads the process environment once and answers whether the card may
    ///     appear. Interactive CellForge is the only surface that keeps it on.
    /// </summary>
    public static bool IsEnabled() => IsEnabled(
        HarborEnv.Get(HarborEnv.Tui),
        Environment.GetEnvironmentVariable("CI"),
        HarborEnv.Get(HarborEnv.SkipOnboarding),
        HarborEnv.Get(HarborEnv.Demo));

    /// <summary>
    ///     The pure form of the guard, so tests can pin every silence case
    ///     without mutating the process environment.
    /// </summary>
    /// <param name="tui">Value of HARBOR_TUI (plain pipes are silent).</param>
    /// <param name="ci">Value of CI (any truthy value is silent).</param>
    /// <param name="skipOnboarding">Value of HARBOR_SKIP_ONBOARDING (E2E/PTY child processes are silent).</param>
    /// <param name="demo">Value of HARBOR_DEMO (scripted demo runs are silent).</param>
    public static bool IsEnabled(string? tui, string? ci, string? skipOnboarding, string? demo)
    {
        if (IsTruthy(ci) || IsTruthy(skipOnboarding) || IsTruthy(demo))
        {
            return false;
        }

        return !string.Equals(tui, "plain", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string? value) =>
        value is { Length: > 0 }
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Called once at host startup. Records the launch and, when due and
    ///     enabled, enqueues one toast. Does nothing at all (no save, no paint)
    ///     when disabled, which is what keeps CI, plain pipes and tests silent.
    /// </summary>
    /// <returns>True when a toast was actually enqueued.</returns>
    public bool TryShow(ToastOverlay toasts, bool? enabled = null)
    {
        if (!(enabled ?? IsEnabled()))
        {
            return false;
        }

        _state = _state.NoteLaunch();
        bool due = _state.ShouldShow(_interval);
        if (due)
        {
            _state = _state.MarkShown();
        }

        _save(_state);
        if (due)
        {
            toasts.Show(Message, ToastKind.Warning);
        }

        return due;
    }

    /// <summary>Persists "do not show again"; the card never appears again.</summary>
    public void Dismiss()
    {
        _state = _state.Dismiss();
        _save(_state);
    }

    /// <summary>
    ///     "Star later": the show already reset the interval, so postponing
    ///     is an explicit acknowledgement that leaves the cadence untouched.
    /// </summary>
    public void StarLater()
    {
        // No-op by design: MarkShown() already moved LastShownAt to this launch,
        // so the next show is due in exactly Interval more launches. Kept as a
        // named action so a host can bind the toast's "later" affordance to it.
    }
}
