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
/// </remarks>
public sealed class ThemeDirectoryWatcher : IDisposable
{
    /// <summary>Poll interval (default 500 ms — imperceptible for theme edits).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly string _directory;
    private readonly Action<HarborTheme>? _onApplied;
    private readonly Action<string>? _onError;
    private readonly Timer _timer;
    private readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Theme applied by the most recent successful reload (null until the first change).</summary>
    public HarborTheme? LastApplied { get; private set; }

    public ThemeDirectoryWatcher(
        string? directory = null,
        Action<HarborTheme>? onApplied = null,
        Action<string>? onError = null,
        bool autoStart = true)
    {
        _directory = directory ?? ThemeStore.DefaultDirectory();
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
            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex.Message);
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
        try
        {
            var result = ThemeJson.Parse(File.ReadAllText(path), TerminalColorPalette.Current);
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
        catch (Exception ex)
        {
            _onError?.Invoke($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}
