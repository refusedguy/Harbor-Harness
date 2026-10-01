using Harbor.DesignSystem;

using CSharpFunctionalExtensions;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Live-reload for custom themes (Claude-Code pattern): polls a theme JSON
/// file on a fixed interval and applies it through
/// <see cref="TerminalColorPalette.Apply" /> whenever it changes. Polling
/// (not FileSystemWatcher) keeps behaviour deterministic across terminals,
/// network mounts and CI. Parse failures keep the last applied theme.
/// </summary>
/// <remarks>
///     Both the stat and the read go through the injected
///     <see cref="IThemeStore" /> (#668). This type used to own the stat itself
///     (<c>File.Exists</c> + <c>File.GetLastWriteTimeUtc</c>) and then reach the
///     read through a <c>JsonThemeLoader.LoadFile</c> that did its own
///     <c>File.Exists</c> + <c>File.ReadAllText</c> + parse — so one
///     read-and-parse-and-stat was spelled out twice, in two files, both in a
///     Presentation assembly, and neither named a shared owner. The store is a
///     required argument on purpose: a defaulted one would let this widget
///     quietly reconstruct its own reader, which is the defect.
/// </para>
/// <para>
///     <b>It applies through <see cref="TerminalColorPalette.Apply" />
///     unconditionally (#479-A6).</b> It used to apply only when no
///     <c>onApplied</c> callback had been supplied — and the one product site
///     that arms this watcher always supplies one, so
///     <c>HARBOR_THEME_FILE</c> / <c>~/.harbor/theme.json</c> polled, announced
///     "theme: live-reload → …", marked the screen dirty and changed no color,
///     while its sibling <c>ThemeDirectoryWatcher</c> repainted on the very
///     same input. The callback was being read as "the caller owns the apply",
///     which no caller has ever meant: the callback here only appends a status
///     line and wakes the render loop. The gate is gone and the sibling's
///     unconditional apply is the shape; the two are compared directly in
///     <c>ThemeWatcherApplyParityTests</c>. The asymmetry predates the port
///     (da29698b, 2026-09-04) and #720 carried it through unchanged, because
///     #720 fixed the duplicated READ and this is the APPLY.
/// </para>
/// </remarks>
public sealed class ThemeFileWatcher : IDisposable
{
    private readonly string _path;
    private readonly IThemeStore _store;
    private readonly Action<HarborTheme>? _onApplied;
    private readonly Action<string>? _onError;
    private readonly Timer _timer;
    private DateTime _lastWriteUtc;

    /// <summary>Poll interval (default 500 ms — imperceptible for theme edits).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Theme applied by the most recent successful reload, or
    ///     <see cref="Maybe{T}.None" /> until the first change (#592).
    /// </summary>
    public Maybe<HarborTheme> LastApplied { get; private set; } = Maybe<HarborTheme>.None;

    public ThemeFileWatcher(
        string path,
        IThemeStore store,
        Action<HarborTheme>? onApplied = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _path = path;
        _store = store;
        _onApplied = onApplied;
        _onError = onError;
        _lastWriteUtc = InitialStamp();
        _timer = new Timer(_ => Poll(), null, Interval, Interval);
    }

    private DateTime InitialStamp()
        => _store.TryGetLastWriteUtc(_path, out var stamp) ? stamp : DateTime.MinValue;

    /// <summary>One poll cycle — exposed for deterministic testing.</summary>
    public void Poll()
    {
        try
        {
            if (!_store.TryGetLastWriteUtc(_path, out var stamp))
            {
                return;
            }

            if (stamp == _lastWriteUtc)
            {
                return;
            }

            _lastWriteUtc = stamp;
            ThemeParseResult result = _store.LoadFile(_path);
            if (result.IsSuccess)
            {
                LastApplied = Maybe.From(result.Theme);
                TerminalColorPalette.Apply(result.Theme);
                _onApplied?.Invoke(result.Theme);
            }
            else
            {
                _onError?.Invoke(result.Error);
            }
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex.Message);
        }
    }

    public void Dispose() => _timer.Dispose();
}
