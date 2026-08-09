using System;
using System.Collections.Generic;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Manages a stack of named overlays (modals, flyouts, pickers).
///     Each overlay is identified by a string id (e.g. "settings", "palette").
///     Escape pops the top overlay.
/// </summary>
public interface IOverlayStack
{
    /// <summary>Currently visible overlay id, or null if none.</summary>
    string? Current { get; }

    /// <summary>All overlay ids in stack order (bottom to top).</summary>
    IReadOnlyList<string> Stack { get; }

    /// <summary>Push a new overlay. No-op if it's already on top.</summary>
    void Push(string id);

    /// <summary>Pop the top overlay and return its id, or null if stack is empty.</summary>
    string? PopTop();

    /// <summary>Event fired when the stack changes (push or pop).</summary>
    event Action<string?, IReadOnlyList<string>>? Changed;
    event Action<string?>? Popped;
}

/// <summary>
///     Default <see cref="IOverlayStack" /> implementation — a simple
///     LIFO stack of string ids with change notifications.
/// </summary>
public sealed class OverlayStackService : IOverlayStack
{
    private readonly Stack<string> _stack = new();

    public string? Current => _stack.Count > 0 ? _stack.Peek() : null;

    public IReadOnlyList<string> Stack => _stack.ToArray();

    public event Action<string?, IReadOnlyList<string>>? Changed;

    public event Action<string?>? Popped;

    public void Push(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_stack.Count > 0 && _stack.Peek() == id) return;
        _stack.Push(id);
        Changed?.Invoke(Current, Stack);
    }

    public string? PopTop()
    {
        if (_stack.Count == 0) return null;
        var popped = _stack.Pop();
        Changed?.Invoke(Current, Stack);
        Popped?.Invoke(popped);
        return popped;
    }
}
