// TR4 (#1223, epic #1195): page-level key bindings + capture-to-bubble router —
// KeyGesture (logical key + modifiers + rune) / KeyBindings (gesture -> action
// id table, Termina Page.KeyBindings analogue) / InputRouting.Route
// (capture -> preview tunnel -> bubble -> remainder). The router REPORTS the
// capture hit as an action id for the host to dispatch — it never invokes
// callbacks itself, so this stays BCL-only with no delegate registry and no
// reflection. Bindings are logical (KeyCode/KeyModifiers/Rune): kitty-vs-legacy
// degradation lives in the parser that produced the KeyEvent, never here.
// Same leaf rule: BCL-only, AOT-compatible, zero Harbor references,
// render-thread owned, chat untouched.

using System.Text;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Routing verdict: which phase consumed the key (diagnostics + tests).
/// </summary>
public enum InputDisposition : byte
{
    /// <summary>A page-level capture binding matched (action id in the out var).</summary>
    CaptureHandled = 0,

    /// <summary>A stack entry's <c>PreviewKey</c> consumed the key (bottom-up tunnel).</summary>
    PreviewHandled = 1,

    /// <summary>A stack entry's <c>HandleKey</c> consumed the key (top-down bubble).</summary>
    FocusHandled = 2,

    /// <summary>Nothing consumed the key; the remainder ran (when provided).</summary>
    Unhandled = 3,
}

/// <summary>
/// One bindable gesture: a named key with exact modifiers, or a character
/// with exact modifiers and an exact rune. Modifier match is EXACT (Ctrl+X
/// does not fire on Ctrl+Shift+X) and rune match is case-EXACT — Shift is a
/// case-shaper per <see cref="KeyModifierGate"/>, so <c>G</c> and <c>g</c>
/// are different gestures and bind separately.
/// Encoder-agnostic: <c>EventType</c>/<c>IsKittyEncoded</c>/<c>Codepoint</c>
/// never participate.
/// </summary>
public readonly record struct KeyGesture(KeyCode Key, KeyModifiers Modifiers, Rune Char)
{
    /// <summary>Named-key gesture (Enter, Escape, F12, arrows, ...).</summary>
    public static KeyGesture ForKey(KeyCode key, KeyModifiers modifiers = KeyModifiers.None) =>
        new(key, modifiers, default);

    /// <summary>Character gesture (<paramref name="c"/> matched exactly).</summary>
    public static KeyGesture ForChar(char c, KeyModifiers modifiers = KeyModifiers.None) =>
        new(KeyCode.Char, modifiers, new Rune(c));

    /// <summary>Whether this gesture fires on <paramref name="key"/>.</summary>
    public bool Matches(KeyEvent key)
    {
        if (key.Modifiers != Modifiers)
        {
            return false;
        }

        if (Key == KeyCode.Char || key.Key == KeyCode.Char)
        {
            return Key == key.Key && Char == key.Character;
        }

        return Key == key.Key;
    }
}

/// <summary>
/// Page-level binding table (Termina <c>Page.KeyBindings</c> analogue): the
/// capture phase of <see cref="InputRouting"/>. Gesture to opaque action id;
/// last bind wins per gesture. Action ids stay strings so the engine never
/// names host behavior — dispatch is the host's job.
/// </summary>
public sealed class KeyBindings
{
    private readonly Dictionary<KeyGesture, string> _table = [];

    /// <summary>Bound gesture count.</summary>
    public int Count => _table.Count;

    /// <summary>Binds <paramref name="gesture"/> to <paramref name="actionId"/> (replaces).</summary>
    public void Bind(KeyGesture gesture, string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        _table[gesture] = actionId;
    }

    /// <summary>Removes the binding for <paramref name="gesture"/>. False when absent.</summary>
    public bool Unbind(KeyGesture gesture) => _table.Remove(gesture);

    /// <summary>Drops every binding.</summary>
    public void Clear() => _table.Clear();

    /// <summary>First-match lookup over the table (insertion order is NOT promised).</summary>
    public bool TryMatch(KeyEvent key, out string? actionId)
    {
        foreach (KeyValuePair<KeyGesture, string> entry in _table)
        {
            if (entry.Key.Matches(key))
            {
                actionId = entry.Value;
                return true;
            }
        }

        actionId = null;
        return false;
    }
}

/// <summary>
/// Capture-to-bubble key router (Termina <c>Page.KeyBindings capture ->
/// focused bubble -> ViewModel.Input remainder</c>, ConsoleEx tunnel-then-
/// bubble, over <see cref="FocusStack"/> instead of a visual tree):
/// <list type="number">
///   <item><description>Capture: <c>capture</c> table first — a hit
///     reports <see cref="InputDisposition.CaptureHandled"/> with the action id;
///     the stack is never consulted (global chords preempt focus).</description></item>
///   <item><description>Preview tunnel: stack bottom-up (oldest to focused),
///     first <c>true</c> wins. Entries with <c>CanFocus</c> false are skipped
///     in place (dynamic flip — the seat is the stack's business).</description></item>
///   <item><description>Bubble: stack top-down from the focused entry, first
///     <c>true</c> wins.</description></item>
///   <item><description>Remainder: <c>remainder</c> runs (when
///     provided) and the verdict is <see cref="InputDisposition.Unhandled"/>.
///     Termina's <c>ViewModel.Input</c> remainder minus R3 — a plain action,
///     null means drop.</description></item>
/// </list>
/// Only stack members participate: a pushed modal owns keys because everything
/// beneath simply is not on the stack (the <c>OverlayLayer</c> modal barrier
/// is the paint/hit-test sibling; this is the key sibling).
/// Indexed loops, zero allocation on the miss path.
/// </summary>
public static class InputRouting
{
    /// <summary>Routes one key through capture, tunnel, bubble and remainder.</summary>
    /// <param name="key">The decoded key.</param>
    /// <param name="capture">Page-level bindings (null = no capture phase).</param>
    /// <param name="stack">Focus stack (null or empty = no tunnel/bubble).</param>
    /// <param name="actionId">The capture hit's action id (null unless
    /// <see cref="InputDisposition.CaptureHandled"/>).</param>
    /// <param name="remainder">Runs when nothing consumed the key (null = drop).</param>
    public static InputDisposition Route(
        KeyEvent key,
        KeyBindings? capture,
        FocusStack? stack,
        out string? actionId,
        Action<KeyEvent>? remainder = null)
    {
        if (capture is not null && capture.TryMatch(key, out actionId))
        {
            return InputDisposition.CaptureHandled;
        }

        actionId = null;

        if (stack is not null)
        {
            for (int i = 0; i < stack.Count; i++)
            {
                IFocusable entry = stack[i];
                if (entry.CanFocus && entry.PreviewKey(key))
                {
                    return InputDisposition.PreviewHandled;
                }
            }

            for (int i = stack.Count - 1; i >= 0; i--)
            {
                IFocusable entry = stack[i];
                if (entry.CanFocus && entry.HandleKey(key))
                {
                    return InputDisposition.FocusHandled;
                }
            }
        }

        remainder?.Invoke(key);
        return InputDisposition.Unhandled;
    }
}
