using Harbor.Tui.ConsoleEx.Input;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Leader-key chord router (ctrl+x pattern): the leader press arms the router,
/// the next key inside the timeout window resolves the chord and fires its
/// bound action; unknown chords disarm silently. Keys while unarmed pass
/// through untouched — the host keeps full routing control.
/// </summary>
public sealed class LeaderKeyRouter
{
    /// <summary>Chord resolve window in ms (arm → key).</summary>
    public const int TimeoutMs = 1500;

    private readonly Dictionary<char, Action> _bindings = [];
    private long _armedAtMs = long.MinValue;

    /// <summary>True while a leader press is awaiting its chord.</summary>
    public bool IsPending => _armedAtMs != long.MinValue;

    /// <summary>Registers a single-character chord. Re-binding replaces.</summary>
    public void Bind(char chord, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _bindings[char.ToLowerInvariant(chord)] = action;
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
        if (_bindings.TryGetValue(chord, out var action))
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
