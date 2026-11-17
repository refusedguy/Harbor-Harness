// StarNagCard.cs — issue #1490: a gentle "star Harbor on GitHub" reminder that
// rides the EXISTING ToastOverlay seat. No new widget primitive, no new overlay
// layer, no new extension axis (#555 freeze): the card is a pure state object
// that feeds the already-shipped toast queue, and the persistence is a small
// file in the Harbor home directory rather than a new registry or seam.
//
// The card is silent in plain/CI/headless/demo processes. IsEnabled() is the
// single guard; the host calls it before doing any work, and TryShow takes an
// explicit `enabled` override so tests can exercise the silence path without
// touching the process environment.

using Harbor.Abstractions.Env;
using Harbor.Abstractions.Filesystem;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;

namespace Harbor.Tui.CellForge.StarNag;

/// <summary>
///     Persisted star-nag counters. Immutable so a paint or a save can never
///     observe a torn snapshot; every mutation returns a new record.
/// </summary>
public sealed record StarNagState
{
    /// <summary>The empty, never-shown state.</summary>
    public static readonly StarNagState Empty = new(0, false, 0);

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
    ///     True when at least <paramref name="interval"/> launches have happened
    ///     since the last show and the user has not dismissed the card for good.
    /// </summary>
    public bool ShouldShow(int interval) => !Dismissed && Launches - LastShownAt >= interval;
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
    ///     The toast text. The trailing keys stand for the two actions the card
    ///     offers: <c>s</c> = star later (postpone one interval),
    ///     <c>n</c> = never (dismiss permanently via <see cref="Dismiss"/>).
    /// </summary>
    public const string Message = "Star Harbor on GitHub? s/n";

    private readonly int _interval;
    private readonly Action<StarNagState> _save;
    private StarNagState _state;

    /// <param name="interval">Minimum launches between two shows. Values below 1 fall back to <see cref="DefaultInterval"/>.</param>
    /// <param name="state">Starting state. Null starts from <see cref="StarNagState.Empty"/>.</param>
    /// <param name="save">Persistence callback. Null makes the card in-memory only (tests).</param>
    public StarNagCard(
        int interval = DefaultInterval,
        StarNagState? state = null,
        Action<StarNagState>? save = null)
    {
        _interval = interval < 1 ? DefaultInterval : interval;
        _state = state ?? StarNagState.Empty;
        _save = save ?? (_ => { });
    }

    /// <summary>Builds the production card over the file-backed store in the Harbor home directory.</summary>
    public static StarNagCard FromFile(int interval = DefaultInterval)
    {
        string path = StorePath;
        StarNagState state = StarNagFileStore.Load(path);
        return new StarNagCard(interval, state, persisted => StarNagFileStore.Save(path, persisted));
    }

    /// <summary>Location of the persisted counters.</summary>
    internal static string StorePath => Path.Combine(HarborPaths.GetHarborHome(), "starnag.txt");

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

/// <summary>
///     Tiny <c>launch;dismissed;lastShownAt</c> file store. Plain text rather
///     than JSON so the cell renderer stays reflection-free under NativeAOT,
///     and all IO failures degrade to "never shown" rather than breaking the
///     interactive startup.
/// </summary>
internal static class StarNagFileStore
{
    internal static StarNagState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return StarNagState.Empty;
            }

            string[] parts = File.ReadAllText(path).Trim().Split(';');
            if (parts.Length != 3)
            {
                return StarNagState.Empty;
            }

            int launches = int.TryParse(parts[0], out int parsedLaunches) ? parsedLaunches : 0;
            bool dismissed = bool.TryParse(parts[1], out bool parsedDismissed) && parsedDismissed;
            int lastShownAt = int.TryParse(parts[2], out int parsedLast) ? parsedLast : 0;
            return new StarNagState(launches, dismissed, lastShownAt);
        }
        catch (IOException)
        {
            return StarNagState.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return StarNagState.Empty;
        }
    }

    internal static void Save(string path, StarNagState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, $"{state.Launches};{state.Dismissed};{state.LastShownAt}");
        }
        catch (IOException)
        {
            // A reminder must never break startup.
        }
        catch (UnauthorizedAccessException)
        {
            // A reminder must never break startup.
        }
    }
}
