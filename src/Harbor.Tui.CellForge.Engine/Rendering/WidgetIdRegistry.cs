// #2496: widget id registry, a stable, user-facing name for one widget, with
// query_one by exact id. This is NOT ElementId (#2361/#2436): ElementId is a
// per-frame identity PATH used by prepaint to reuse records across frames,
// while a widget id is a name that stays the same across frames so a caller can
// find the widget again. There are no selectors and no CSS here, just an
// ordinal string key. BCL-only, AOT-compatible, render-thread owned (same
// ownership as ElementIdStack), not thread-safe.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Maps a widget id to one <see cref="INode"/> and answers
/// <see cref="QueryOne(string)"/> by exact id.
/// </summary>
/// <remarks>
/// Ids are ordinal strings, compared with <see cref="StringComparer.Ordinal"/>.
/// A duplicate id is an error: <see cref="Register(string, INode)"/> throws and
/// <see cref="TryRegister(string, INode)"/> refuses without overwriting. Do not
/// confuse this with <see cref="ElementId"/>, which is a frame-identity path
/// rather than a stable widget name. The instance is owned by one render thread
/// and is not thread-safe.
/// </remarks>
internal sealed class WidgetIdRegistry
{
    private readonly Dictionary<string, INode> _widgets = new(StringComparer.Ordinal);

    /// <summary>Number of widgets currently registered.</summary>
    internal int Count => _widgets.Count;

    /// <summary>
    /// Registers <paramref name="widget"/> under <paramref name="id"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="id"/> is already registered. A duplicate is
    /// an explicit error, never a silent overwrite.
    /// </exception>
    internal void Register(string id, INode widget)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(widget);

        if (!_widgets.TryAdd(id, widget))
        {
            throw new InvalidOperationException($"A widget with id '{id}' is already registered.");
        }
    }

    /// <summary>
    /// Attempts to register without throwing. Returns <see langword="false"/>
    /// and leaves the existing widget untouched when the id is already taken;
    /// returns <see langword="true"/> on a fresh id.
    /// </summary>
    internal bool TryRegister(string id, INode widget)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(widget);

        return _widgets.TryAdd(id, widget);
    }

    /// <summary>
    /// Removes the widget registered under <paramref name="id"/>.
    /// Returns <see langword="true"/> when a widget was removed,
    /// <see langword="false"/> when the id was not registered.
    /// </summary>
    internal bool Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _widgets.Remove(id);
    }

    /// <summary>
    /// Returns the widget registered under the exact <paramref name="id"/>, or
    /// <see langword="null"/> when no widget has that id. There is no selector
    /// syntax. The string is matched verbatim and case-sensitively.
    /// </summary>
    internal INode? QueryOne(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _widgets.TryGetValue(id, out var widget) ? widget : null;
    }
}
