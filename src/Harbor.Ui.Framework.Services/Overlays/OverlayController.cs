using System;
using System.Collections.Generic;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Manages a stack of named overlays (modals, flyouts, pickers) and maps
///     overlay ids to boolean flag setters. Replaces the ad-hoc dictionaries
///     and reflection in MainViewModel / MainViewModelBase.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly IOverlayStack _stack;
    private readonly Dictionary<string, Action<bool>> _setters = new();
    private bool _disposed;

    public OverlayController(IOverlayStack? stack = null)
    {
        _stack = stack ?? new OverlayStackService();
        _stack.Popped += OnPopped;
        _stack.Changed += (_, _) => HasOverlay = _stack.Current is not null;
        HasOverlay = _stack.Current is not null;
    }

    public bool HasOverlay { get; private set; }

    public void Register(string id, Action<bool> setter)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Overlay id cannot be empty.", nameof(id));
        _setters[id] = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    public void Open(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_setters.TryGetValue(id, out var setter))
            setter(true);
        _stack.Push(id);
    }

    public void Close(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_setters.TryGetValue(id, out var setter))
            setter(false);
    }

    public bool CloseTop()
    {
        var top = _stack.Current;
        if (top is null) return false;
        Close(top);
        _stack.PopTop();
        return true;
    }

    private void OnPopped(string? id)
    {
        if (id is not null)
            Close(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stack.Popped -= OnPopped;
        _disposed = true;
    }
}
