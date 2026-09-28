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
/// <remarks>
///     <para>
///         Lifetime contract: <see cref="IOverlayStack" /> is a singleton, so a
///         controller is the disposable end of the subscription. Every renderer
///         swap (theme switch, backend hot-swap, golden run) builds a fresh
///         controller against that same stack — a controller that does not
///         detach on <see cref="Dispose" /> stays reachable from the stack for
///         the process lifetime and keeps writing overlay flags into a renderer
///         that no longer exists.
///     </para>
///     <para>
///         Post-dispose the controller is inert: <see cref="Register" /> and
///         <see cref="Open" /> throw <see cref="ObjectDisposedException" />,
///         <see cref="Close" /> no-ops, <see cref="CloseTop" /> returns
///         <c>false</c>, <see cref="HasOverlay" /> stays <c>false</c>, and a stack
///         event delivered to an already-detached handler is a no-op rather
///         than a paint.
///     </para>
/// </remarks>
public sealed class OverlayController : IDisposable
{
    private readonly IOverlayStack _stack;
    private readonly Dictionary<string, Action<bool>> _setters = new();
    private bool _disposed;

    public OverlayController(IOverlayStack? stack = null)
    {
        _stack = stack ?? new OverlayStackService();
        // Both handlers are named methods so Dispose can detach them. A lambda
        // would root this controller in the singleton stack forever (#476).
        _stack.Popped += OnPopped;
        _stack.Changed += OnChanged;
        HasOverlay = _stack.Current is not null;
    }

    /// <summary>Whether any overlay is currently on the stack.</summary>
    /// <remarks>Always <c>false</c> once disposed.</remarks>
    public bool HasOverlay { get; private set; }

    /// <summary>Whether <see cref="Dispose" /> has run and the stack is detached.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Maps an overlay id to the boolean flag it drives.</summary>
    /// <exception cref="ObjectDisposedException">The controller was disposed.</exception>
    public void Register(string id, Action<bool> setter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Overlay id cannot be empty.", nameof(id));
        _setters[id] = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    /// <summary>Opens an overlay: drives its flag to <c>true</c> and pushes it.</summary>
    /// <exception cref="ObjectDisposedException">
    ///     The controller was disposed. Opening on a dead renderer is the bug this
    ///     guard exists for — a silent no-op would hide it.
    /// </exception>
    public void Open(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(id)) return;
        if (_setters.TryGetValue(id, out var setter))
            setter(true);
        _stack.Push(id);
    }

    /// <summary>
    ///     Closes an overlay: drives its flag to <c>false</c>. Idempotent and
    ///     safe after disposal — teardown races (deferred dispatcher callbacks)
    ///     may still call this, and closing is already the released state.
    /// </summary>
    public void Close(string id)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(id)) return;
        if (_setters.TryGetValue(id, out var setter))
            setter(false);
    }

    /// <summary>Closes and pops the top overlay. Returns <c>false</c> when the stack is empty.</summary>
    public bool CloseTop()
    {
        if (_disposed) return false;
        var top = _stack.Current;
        if (top is null) return false;
        Close(top);
        _stack.PopTop();
        return true;
    }

    private void OnPopped(string? id)
    {
        // Guard, not just detachment: a raise whose invocation list was captured
        // before Dispose still reaches this handler. Without the guard it would
        // paint into a renderer that is already gone (#476).
        if (_disposed) return;
        if (id is not null)
            Close(id);
    }

    private void OnChanged(string? current, IReadOnlyList<string> _)
    {
        if (_disposed) return;
        HasOverlay = _stack.Current is not null;
    }

    /// <summary>
    ///     Detaches from the singleton <see cref="IOverlayStack" /> and drops the
    ///     id→setter map. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stack.Popped -= OnPopped;
        _stack.Changed -= OnChanged;
        _setters.Clear();
        HasOverlay = false;
    }
}
