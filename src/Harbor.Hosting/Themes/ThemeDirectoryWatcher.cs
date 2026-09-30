using Harbor.DesignSystem;

namespace Harbor.Hosting.Themes;

/// <summary>
/// Live-reload for the theme marketplace: polls a themes directory on a fixed
/// interval and applies changed theme JSON files through
/// <see cref="TerminalColorPalette.Apply" />. Polling (not FileSystemWatcher)
/// keeps behaviour deterministic across terminals, network mounts and CI.
/// Invalid files report through the onError callback and keep the last
/// applied theme. Expose <see cref="Poll" /> for deterministic tests.
/// </summary>
/// <remarks>
/// <para>
///     #536 moved this type into <c>Harbor.Hosting.Themes</c>; it used to live in
///     <c>Harbor.DesignSystem</c>. The reason is the same as
///     <see cref="ThemeStore" />'s and is recorded there in full: a token leaf with
///     an empty allowed-reference set has no business enumerating a directory and
///     stat-ing files on a timer.
/// </para>
/// <para>
///     #536 also calls the directory walk "unbounded", and that half is still here.
///     It is a poll every 500 ms over a top-directory-only <c>*.json</c>
///     enumeration — bounded in depth, unbounded in entry count — on a timer thread,
///     not the render loop. So it is not the UI-thread hazard the issue described,
///     but a directory with a great many files in it costs one stat per file per
///     tick. Recorded here so the next reader does not re-derive it from the issue.
/// </para>
/// <para>
///     <b>Not merged with ThemeFileWatcher, on purpose (#479-A6).</b> The two look
///     alike — <c>Timer</c> → poll → stamp → load → callback — and #479 offered a
///     shared <c>PollingWatcher</c> base. They are different requirements that
///     happen to share a silhouette, and only the READ is common:
///     <list type="bullet">
///         <item>
///             The FILE SET differs. This type enumerates a directory each tick and
///             GCs the stamps of paths that vanished, so a theme can appear and
///             disappear. <c>ThemeFileWatcher</c> holds one named path and has no
///             state for "it is gone".
///         </item>
///         <item>
///             The CHANGE SIGNAL differs: a stamp dictionary with per-path eviction
///             here, one scalar <c>DateTime</c> there.
///         </item>
///         <item>One change loads one file here and N files there.</item>
///     </list>
///     A base class would override <c>Poll</c> in both, so it would hold a
///     <c>Timer</c> and decide nothing — and it could not be placed anyway, the
///     only common leaf being the zero-reference token sheet. So the read is one
///     owner's job (<see cref="ThemeStore" />, via <see cref="IThemeStore" />) and
///     the poll is each watcher's own. Both now use the port, which is the part
///     that genuinely was duplicated. <c>ThemeFileWatcher</c> says the same thing
///     about this pair; see <c>ThemeStoreSeamRules</c> for the full argument.
/// </para>
/// </remarks>
public sealed class ThemeDirectoryWatcher : IDisposable
{
    /// <summary>Poll interval (default 500 ms — imperceptible for theme edits).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly string _directory;
    private readonly IThemeStore _store;
    private readonly Action<HarborTheme>? _onApplied;
    private readonly Action<string>? _onError;
    private readonly Timer _timer;
    private readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Theme applied by the most recent successful reload (null until the first change).</summary>
    public HarborTheme? LastApplied { get; private set; }

    /// <summary>Creates a watcher over a theme directory.</summary>
    /// <param name="directory">
    ///     Directory to watch, or <c>null</c> for <see cref="ThemeStore.DefaultDirectory" />.
    /// </param>
    /// <param name="store">
    ///     Required, and required rather than optional on purpose (#479-A6): a defaulted
    ///     store would let this type quietly build its own reader, which is the defect
    ///     the port exists to remove. The stamp and the read both go through it.
    /// </param>
    /// <param name="onApplied">Called with each theme that parsed; the palette is also applied.</param>
    /// <param name="onError">Called with a per-file-prefixed message when a file fails.</param>
    /// <param name="autoStart">When false the timer is disabled, so only <see cref="Poll" /> runs.</param>
    public ThemeDirectoryWatcher(
        string? directory,
        IThemeStore store,
        Action<HarborTheme>? onApplied = null,
        Action<string>? onError = null,
        bool autoStart = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        _directory = directory ?? ThemeStore.DefaultDirectory();
        _store = store;
        _onApplied = onApplied;
        _onError = onError;
        _timer = autoStart ? new Timer(_ => Poll(), null, Interval, Interval) : DisabledTimer();
    }

    private static Timer DisabledTimer() => new(_ => { }, null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>One poll cycle — exposed for deterministic testing.</summary>
    public void Poll()
    {
        string[] files;
        try
        {
            files = Directory.Exists(_directory)
                ? Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex.Message);
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            seen.Add(path);

            // The stamp comes from the port too, not just the read: the enumeration
            // above is this type's own job (it is the file set), but "when did this
            // file change" is a port member, so a bare File.GetLastWriteTimeUtc here
            // was a second answer to a question the store already answers.
            if (!_store.TryGetLastWriteUtc(path, out var stamp))
            {
                _onError?.Invoke($"{Path.GetFileName(path)}: could not stat");
                continue;
            }

            if (_stamps.TryGetValue(path, out var known) && known == stamp)
            {
                continue;
            }

            _stamps[path] = stamp;
            Apply(path);
        }

        foreach (string gone in _stamps.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _stamps.Remove(gone);
        }
    }

    private void Apply(string path)
    {
        // Read-and-parse through the port, like its sibling ThemeFileWatcher. This used
        // to be its own File.ReadAllText + ThemeJson.Parse, which was the third answer
        // to one question (#479-A6) and the last one standing after #720. The store's
        // LoadFile never throws — a missing, unreadable or malformed document all come
        // back as a failed result — so the try/catch that used to wrap this body is
        // gone with the duplication: behaviour for every caller is unchanged, including
        // the per-file error prefix.
        ThemeParseResult result = _store.LoadFile(path);
        if (result.IsSuccess)
        {
            LastApplied = result.Theme;
            TerminalColorPalette.Apply(result.Theme);
            _onApplied?.Invoke(result.Theme);
        }
        else
        {
            _onError?.Invoke($"{Path.GetFileName(path)}: {result.Error}");
        }
    }

    public void Dispose() => _timer.Dispose();
}
