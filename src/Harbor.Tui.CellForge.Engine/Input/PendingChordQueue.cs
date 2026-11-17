// #2359: the single pending-chord queue. The 2000 ms arm window and the one
// staged result live here; key interpretation stays in the router that owns
// the chord vocabulary (LeaderKeyRouter for Ctrl+X). BCL-only, AOT-compatible,
// render-thread owned — same leaf rule as the rest of engine/Input.
//
// GPUI's dispatch_key returns `pending` plus `bindings` to replay. This is the
// engine half of that `pending`: LeaderKeyRouter no longer keeps its own arm
// timestamp or staged AppMsg, so there is exactly one timing implementation.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// One pending chord: an arm timestamp with a fixed resolve window plus a
/// single staged result the host drains. Not thread-safe — render thread only,
/// like <see cref="FocusStack"/> and <see cref="BindingsMap"/>.
/// </summary>
/// <typeparam name="T">Opaque staged result type (e.g. a host store message).</typeparam>
public sealed class PendingChordQueue<T> where T : class
{
    /// <summary>Chord resolve window in ms. Mirrors opencode's leader timeout.</summary>
    public const int TimeoutMs = 2000;

    private long _armedAtMs = long.MinValue;
    private T? _staged;

    /// <summary>True while a chord is awaiting its follow-up key.</summary>
    public bool IsPending => _armedAtMs != long.MinValue;

    /// <summary>The staged result, if any, before the host drains it.</summary>
    public T? Pending => _staged;

    /// <summary>True when the arm is still inside its resolve window.</summary>
    public bool IsWithinWindow(long nowMs) => IsPending && nowMs - _armedAtMs <= TimeoutMs;

    /// <summary>Arms the queue. False when a chord is already pending.</summary>
    public bool TryArm(long nowMs)
    {
        if (IsPending)
        {
            return false;
        }

        _armedAtMs = nowMs;
        return true;
    }

    /// <summary>
    /// Disarms the queue and reports whether the chord arrived inside the
    /// window. False both when nothing was armed and when the window expired.
    /// </summary>
    public bool TryResolve(long nowMs)
    {
        if (!IsPending)
        {
            return false;
        }

        bool within = nowMs - _armedAtMs <= TimeoutMs;
        _armedAtMs = long.MinValue;
        return within;
    }

    /// <summary>Clears a pending arm without resolving. False when not armed.</summary>
    public bool Cancel()
    {
        if (!IsPending)
        {
            return false;
        }

        _armedAtMs = long.MinValue;
        return true;
    }

    /// <summary>Stages a result for the host to drain.</summary>
    public void Stage(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _staged = value;
    }

    /// <summary>Drains the staged result, leaving the queue empty.</summary>
    public T? Take()
    {
        T? value = _staged;
        _staged = null;
        return value;
    }

    /// <summary>Drops both the arm and any staged result.</summary>
    public void Clear()
    {
        _armedAtMs = long.MinValue;
        _staged = null;
    }
}
