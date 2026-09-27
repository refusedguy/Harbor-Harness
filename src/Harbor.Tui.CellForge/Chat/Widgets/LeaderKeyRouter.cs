using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Leader-key chord router (ctrl+x pattern): the leader press arms the router,
/// the next key inside the timeout window resolves the chord and fires its
/// bound action; unknown chords disarm silently. Keys while unarmed pass
/// through untouched — the host keeps full routing control.
/// </summary>
/// <remarks>
/// Epic C: chord meaning travels as <see cref="UiMsg"/> — hosts bind chords
/// with <see cref="Bind(char, UiMsg, Action?)"/> and drain the resolved message
/// via <see cref="TakePendingMsg"/> to <c>UiStore.Dispatch</c> it, so every
/// renderer shares one reducer-owned experience. The legacy
/// <see cref="Bind(char, Action)"/> overload stays for action-only chords
/// (palette toggles, slash hand-offs) that have no reducer transition yet.
/// </remarks>
public sealed class LeaderKeyRouter
{
    /// <summary>Chord resolve window in ms (arm → key).</summary>
    public const int TimeoutMs = 1500;

    private readonly Dictionary<char, Action> _bindings = [];
    private readonly Dictionary<char, (UiMsg Msg, Action? Run)> _messages = [];
    private long _armedAtMs = long.MinValue;

    /// <summary>True while a leader press is awaiting its chord.</summary>
    public bool IsPending => _armedAtMs != long.MinValue;

    /// <summary>
    /// Last msg-bound chord resolved by <see cref="HandleKey"/>, awaiting a
    /// host <see cref="TakePendingMsg"/> drain. Null when no msg-bound chord
    /// fired since the last drain.
    /// </summary>
    public UiMsg? PendingMsg { get; private set; }

    /// <summary>Registers a single-character chord. Re-binding replaces.</summary>
    public void Bind(char chord, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _bindings[char.ToLowerInvariant(chord)] = action;
    }

    /// <summary>
    /// Registers a single-character chord whose meaning is a store message:
    /// resolving it stages <paramref name="msg"/> in <see cref="PendingMsg"/>
    /// for the host to dispatch, then runs <paramref name="run"/> (local paint
    /// side, e.g. waking the frame loop). Re-binding replaces.
    /// </summary>
    public void Bind(char chord, UiMsg msg, Action? run = null)
    {
        ArgumentNullException.ThrowIfNull(msg);
        _messages[char.ToLowerInvariant(chord)] = (msg, run);
    }

    /// <summary>
    /// Drains the staged chord message (if any) — the host dispatches the
    /// returned value through <c>UiStore.Dispatch</c>. Mirrors the palette's
    /// <c>TakePendingCommit</c> hand-off: no host-side chord stacks.
    /// </summary>
    public UiMsg? TakePendingMsg()
    {
        var msg = PendingMsg;
        PendingMsg = null;
        return msg;
    }

    /// <summary>
    /// Feeds a key event. Returns true when the event was consumed (the leader
    /// press itself, a resolved chord, or a failed chord attempt). The bound
    /// action fires synchronously on resolution.
    /// </summary>
    public bool HandleKey(in KeyEvent key, long nowMs)
    {
        if (IsPending && nowMs - _armedAtMs > TimeoutMs)
        {
            _armedAtMs = long.MinValue; // window expired
        }

        if (!IsPending)
        {
            if (IsLeaderPress(key))
            {
                _armedAtMs = nowMs;
                return true;
            }

            return false;
        }

        _armedAtMs = long.MinValue;
        if (key.Key != KeyCode.Char || key.Modifiers != KeyModifiers.None)
        {
            return true; // armed but not a plain char — consume and disarm
        }

        char chord = char.ToLowerInvariant((char)key.Character.Value);
        if (_messages.TryGetValue(chord, out var bound))
        {
            PendingMsg = bound.Msg;
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
