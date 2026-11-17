using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Leader-key chord router (ctrl+x pattern, opencode keybind.ts steal, #1175):
/// the leader press arms the router, the next key inside the timeout window
/// resolves the chord and fires its bound action; unknown chords disarm
/// silently. Keys while unarmed pass through untouched — the host keeps full
/// routing control.
/// </summary>
/// <remarks>
/// The 2000 ms window mirrors opencode's leader timeout (config/keybind.ts).
/// <see cref="ChatAction.None"/> is the unbound convention on the store side:
/// <see cref="Unbind"/> returns a chord to it. Any printable char (including
/// ',') can be a chord — comma bindings need no special case. Escape while
/// armed disarms (escape-clears-pending, context/keymap.tsx).
/// Epic C: chord meaning travels as <see cref="AppMsg"/> — hosts bind chords
/// with <see cref="Bind(char, AppMsg, Action?)"/> and drain the resolved message
/// via <see cref="TakePendingMsg"/> to <c>UiStore.Dispatch</c> it, so every
/// renderer shares one reducer-owned experience. The legacy
/// <see cref="Bind(char, Action)"/> overload stays for action-only chords
/// (palette toggles, slash hand-offs) that have no reducer transition yet.
/// #2359: the arm window and the staged message live in the shared engine
/// <see cref="PendingChordQueue{T}"/>; this router owns only the chord vocabulary.
/// </remarks>
public sealed class LeaderKeyRouter
{
    /// <summary>Chord resolve window in ms (arm → key). Mirrors opencode's leader timeout.</summary>
    public const int TimeoutMs = PendingChordQueue<AppMsg>.TimeoutMs;

    private readonly Dictionary<char, Action> _bindings = [];
    private readonly Dictionary<char, (AppMsg Msg, Action? Run)> _messages = [];

    // #2359: one pending-chord queue for every chord router. The 2000 ms arm
    // window and the staged message live in the engine primitive, so this
    // router owns only the chord vocabulary (Ctrl+X and its bound chars) —
    // no second timing implementation, no second pending state.
    private readonly PendingChordQueue<AppMsg> _pending = new();

    /// <summary>True while a leader press is awaiting its chord.</summary>
    public bool IsPending => _pending.IsPending;

    /// <summary>
    /// Last msg-bound chord resolved by <see cref="HandleKey"/>, awaiting a
    /// host <see cref="TakePendingMsg"/> drain. Null when no msg-bound chord
    /// fired since the last drain.
    /// </summary>
    public AppMsg? PendingMsg => _pending.Pending;

    /// <summary>Registers a single-character chord. Re-binding replaces.</summary>
    public void Bind(char chord, Action action)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(action);
        _bindings[char.ToLowerInvariant(chord)] = action;
    }

    /// <summary>
    /// Registers a single-character chord whose meaning is a store message:
    /// resolving it stages <paramref name="msg"/> in <see cref="PendingMsg"/>
    /// for the host to dispatch, then runs <paramref name="run"/> (local paint
    /// side, e.g. waking the frame loop). Re-binding replaces.
    /// </summary>
    public void Bind(char chord, AppMsg msg, Action? run = null)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(msg);
        _messages[char.ToLowerInvariant(chord)] = (msg, run);
    }

    /// <summary>
    /// Drains the staged chord message (if any) — the host dispatches the
    /// returned value through <c>UiStore.Dispatch</c>. Mirrors the palette's
    /// <c>TakePendingCommit</c> hand-off: no host-side chord stacks.
    /// </summary>
    public AppMsg? TakePendingMsg() => _pending.Take();

    /// <summary>
    /// Removes a chord binding (action and message forms). The chord returns
    /// to the unbound convention: resolving it disarms silently, and the
    /// store side stays <see cref="ChatAction.None"/>.
    /// </summary>
    public void Unbind(char chord)
    {
        char key = char.ToLowerInvariant(chord);
        _ = _bindings.Remove(key);
        _ = _messages.Remove(key);
    }

    /// <summary>
    /// Disarms a pending leader press without resolving (escape-clears-pending).
    /// Returns true when a pending press was actually cleared.
    /// </summary>
    public bool Cancel() => _pending.Cancel();

    /// <summary>
    /// Feeds a key event. Returns true when the event was consumed (the leader
    /// press itself, a resolved chord, or a failed chord attempt). The bound
    /// action fires synchronously on resolution. Escape while armed disarms
    /// (escape-clears-pending) and is consumed.
    /// </summary>
    public bool HandleKey(in KeyEvent key, long nowMs)
    {
        // Same order as before: expiry first, then the unarmed leader check,
        // then escape. Only the state moved — into the shared queue.
        if (_pending.IsPending && !_pending.IsWithinWindow(nowMs))
        {
            _ = _pending.Cancel(); // window expired
        }

        if (!_pending.IsPending)
        {
            if (IsLeaderPress(key))
            {
                _ = _pending.TryArm(nowMs);
                return true;
            }

            return false;
        }

        if (key.Key == KeyCode.Escape)
        {
            _ = _pending.Cancel(); // escape-clears-pending
            return true;
        }

        _ = _pending.TryResolve(nowMs); // in-window; disarm
        if (key.Key != KeyCode.Char || !key.Modifiers.IsUnmodified())
        {
            return true; // armed but not a plain char — consume and disarm
        }

        char chord = char.ToLowerInvariant((char)key.Character.Value);
        if (_messages.TryGetValue(chord, out var bound))
        {
            _pending.Stage(bound.Msg);
            bound.Run?.Invoke();
        }
        else if (_bindings.TryGetValue(chord, out var action))
        {
            action();
        }

        return true;
    }

    private static bool IsLeaderPress(in KeyEvent key) =>
        key.Key == KeyCode.Char
        && (key.Modifiers & KeyModifiers.Ctrl) != 0
        && char.ToLowerInvariant((char)key.Character.Value) == 'x';
}
