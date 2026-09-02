using Harbor.DesignSystem;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Live-reload for custom themes (Claude-Code pattern): polls a theme JSON
/// file on a fixed interval and applies it through
/// <see cref="TerminalColorPalette.Apply" /> whenever it changes. Polling
/// (not FileSystemWatcher) keeps behaviour deterministic across terminals,
/// network mounts and CI. Parse failures keep the last applied theme.
/// </summary>
public sealed class ThemeFileWatcher : IDisposable
{
    private readonly string _path;
    private readonly Action<HarborTheme>? _onApplied;
    private readonly Action<string>? _onError;
    private readonly Timer _timer;
    private DateTime _lastWriteUtc;

    /// <summary>Poll interval (default 500 ms — imperceptible for theme edits).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>Theme applied by the most recent successful reload (null until the first change).</summary>
    public HarborTheme? LastApplied { get; private set; }

    public ThemeFileWatcher(string path, Action<HarborTheme>? onApplied = null, Action<string>? onError = null)
    {
        _path = path;
        _onApplied = onApplied;
        _onError = onError;
        _lastWriteUtc = InitialStamp();
        _timer = new Timer(_ => Poll(), null, Interval, Interval);
    }

    private DateTime InitialStamp() => File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

    /// <summary>One poll cycle — exposed for deterministic testing.</summary>
    public void Poll()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _lastWriteUtc)
            {
                return;
            }

            _lastWriteUtc = stamp;
            var result = JsonThemeLoader.LoadFile(_path);
            if (result.IsSuccess)
            {
                LastApplied = result.Value;
                TerminalColorPalette.Apply(result.Value);
                _onApplied?.Invoke(result.Value);
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
