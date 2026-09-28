using CSharpFunctionalExtensions;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     JSON theme file role: load a theme document, apply it, and live-reload it
///     from disk. Split out of <see cref="IThemeService" /> (ISP, #469) because
///     the terminal renderer owns this concern (<c>JsonThemeLoader</c>) while
///     desktop shells only raise a notification for it.
/// </summary>
public interface IThemeWatcher
{
    /// <summary>Load a theme JSON file from disk.</summary>
    public Result<string> LoadJson(string path);

    /// <summary>Parse and apply a theme from a JSON string.</summary>
    public Result ApplyJson(string json);

    /// <summary>Raised after a JSON theme is applied successfully.</summary>
    public event EventHandler<string>? ThemeJsonApplied;

    /// <summary>Watch a theme JSON file for live reload.</summary>
    public IDisposable Watch(string path);

    /// <summary>
    ///     Watch a theme JSON file for live reload, surfacing non-fatal
    ///     watcher errors (parse failures, IO) to <paramref name="onError" />.
    ///     Live-reload resumes on the next write. Default routes to
    ///     <see cref="Watch(string)" /> so existing implementers stay source-compatible.
    /// </summary>
    public IDisposable Watch(string path, Action<string>? onError) => Watch(path);
}
