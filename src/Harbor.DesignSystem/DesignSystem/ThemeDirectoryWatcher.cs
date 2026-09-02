namespace Harbor.DesignSystem;

/// <summary>
/// Live-reload for the theme marketplace: polls a themes directory on a fixed
/// interval and applies changed theme JSON files through
/// <see cref="TerminalColorPalette.Apply" />. Polling (not FileSystemWatcher)
/// keeps behaviour deterministic across terminals, network mounts and CI.
/// Invalid files report through the onError callback and keep the last
/// applied theme. Expose <see cref="Poll" /> for deterministic tests.
/// </summary>
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
