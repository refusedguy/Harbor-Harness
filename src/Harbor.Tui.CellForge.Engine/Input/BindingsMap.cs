// T6 (#1188, epic #1155): bindings as data (textual binding.py steal) —
// Binding record (key/action/description/show/key_display/priority/id/system/
// group) + KeySpec parser ("ctrl+j", "space", "x") + BindingsMap (per-scope
// table, focus-to-app merge, id-based keymap overrides with clash detect,
// shown_keys for footer/help). Per-widget BINDINGS migrate one by one through
// IBindingScope; ChatKeyMap / LeaderKeyRouter stay untouched this slice.
// Same leaf rule as KeyBindings.cs: BCL-only, AOT-compatible, zero Harbor
// references, render-thread owned, chat untouched.

using System.Text;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// One bindable key as data (textual <c>Binding</c> analogue): a stable
/// <see cref="Id"/> naming the binding, the <see cref="Gesture"/> that fires
/// it, the opaque <see cref="Action"/> id the host dispatches, and the
/// metadata a footer/help panel renders for free (<see cref="Description"/>,
/// <see cref="Show"/>, <see cref="KeyDisplay"/>, <see cref="Group"/>).
/// Priority breaks gesture ties inside one map; <see cref="System"/> marks
/// builtins a user keymap should not unbind by accident (advisory only).
/// Immutable — rebinds replace the record, never mutate it.
/// </summary>
/// <param name="Id">Stable binding id — the keymap-override handle.</param>
/// <param name="Gesture">The gesture that fires this binding.</param>
/// <param name="Action">Opaque action id the host dispatches.</param>
/// <param name="Description">Human-readable label (footer/help).</param>
/// <param name="Show">Whether <see cref="BindingsMap.ShownKeys"/> lists it.</param>
/// <param name="KeyDisplay">Display override; null formats from the gesture.</param>
/// <param name="Priority">Higher wins on same-map gesture ties.</param>
/// <param name="System">Advisory builtin flag (overrides still apply).</param>
/// <param name="Group">Which-key group (session / panel / diff …).</param>
public sealed record Binding(
    string Id,
    KeyGesture Gesture,
    string Action,
    string Description = "",
    bool Show = true,
    string? KeyDisplay = null,
    int Priority = 0,
    bool System = false,
    string? Group = null)
{
    /// <summary>What a footer/help row prints for this binding.</summary>
    public string DisplayKey => KeyDisplay ?? KeySpec.Format(Gesture);
}

/// <summary>
/// Textual key-format parser ("ctrl+j", "ctrl+alt+k", "space", "?", "enter",
/// "f12", "escape", "up", "x"): modifiers joined with '+', last token the key.
/// Single characters match exactly (case-EXACT per <see cref="KeyGesture"/>);
/// named keys are case-insensitive. Unknown specs fail without throwing.
/// </summary>
public static class KeySpec
{
    /// <summary>Parses <paramref name="spec"/> into <paramref name="gesture"/>.</summary>
    /// <returns>False when the spec is empty, has no key, or names an unknown key.</returns>
    public static bool TryParse(string? spec, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        string[] parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var mods = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= KeyModifiers.Ctrl;
                    break;
                case "shift":
                    mods |= KeyModifiers.Shift;
                    break;
                case "alt":
                case "option":
                    mods |= KeyModifiers.Alt;
                    break;
                case "meta":
                case "super":
                case "win":
                case "cmd":
                case "command":
                    mods |= KeyModifiers.Meta;
                    break;
                default:
                    return false;
            }
        }

        string key = parts[^1];
        if (key.Length == 1)
        {
            gesture = KeyGesture.ForChar(key[0], mods);
            return true;
        }

        switch (key.ToLowerInvariant())
        {
            case "space":
                gesture = KeyGesture.ForChar(' ', mods);
                return true;
            case "enter":
            case "return":
                gesture = KeyGesture.ForKey(KeyCode.Enter, mods);
                return true;
            case "tab":
                gesture = KeyGesture.ForKey(KeyCode.Tab, mods);
                return true;
            case "backspace":
            case "bspace":
                gesture = KeyGesture.ForKey(KeyCode.Backspace, mods);
                return true;
            case "escape":
            case "esc":
                gesture = KeyGesture.ForKey(KeyCode.Escape, mods);
                return true;
            case "up":
                gesture = KeyGesture.ForKey(KeyCode.Up, mods);
                return true;
            case "down":
                gesture = KeyGesture.ForKey(KeyCode.Down, mods);
                return true;
            case "left":
                gesture = KeyGesture.ForKey(KeyCode.Left, mods);
                return true;
            case "right":
                gesture = KeyGesture.ForKey(KeyCode.Right, mods);
                return true;
            case "home":
                gesture = KeyGesture.ForKey(KeyCode.Home, mods);
                return true;
            case "end":
                gesture = KeyGesture.ForKey(KeyCode.End, mods);
                return true;
            case "pageup":
            case "page_up":
            case "pgup":
                gesture = KeyGesture.ForKey(KeyCode.PageUp, mods);
                return true;
            case "pagedown":
            case "page_down":
            case "pgdn":
            case "pgdown":
                gesture = KeyGesture.ForKey(KeyCode.PageDown, mods);
                return true;
            case "insert":
            case "ins":
                gesture = KeyGesture.ForKey(KeyCode.Insert, mods);
                return true;
            case "delete":
            case "del":
                gesture = KeyGesture.ForKey(KeyCode.Delete, mods);
                return true;
            case "f1":
            case "f2":
            case "f3":
            case "f4":
            case "f5":
            case "f6":
            case "f7":
            case "f8":
            case "f9":
            case "f10":
            case "f11":
            case "f12":
                if (int.TryParse(key[1..], out int n) && n >= 1 && n <= 12)
                {
                    gesture = KeyGesture.ForKey((KeyCode)((int)KeyCode.F1 + (n - 1)), mods);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Canonical display for a gesture ("ctrl+j", "space", "enter").</summary>
    public static string Format(KeyGesture gesture)
    {
        StringBuilder sb = new();
        if ((gesture.Modifiers & KeyModifiers.Ctrl) != 0)
        {
            sb.Append("ctrl+");
        }

        if ((gesture.Modifiers & KeyModifiers.Shift) != 0)
        {
            sb.Append("shift+");
        }

        if ((gesture.Modifiers & KeyModifiers.Alt) != 0)
        {
            sb.Append("alt+");
        }

        if ((gesture.Modifiers & KeyModifiers.Meta) != 0)
        {
            sb.Append("meta+");
        }

        if (gesture.Key == KeyCode.Char)
        {
            char c = (char)gesture.Char.Value;
            sb.Append(c == ' ' ? "space" : char.ToLowerInvariant(c).ToString());
            return sb.ToString();
        }

        sb.Append(gesture.Key.ToString().ToLowerInvariant());
        return sb.ToString();
    }
}

/// <summary>
/// Opt-in per-widget bindings seam (textual per-widget <c>BINDINGS</c>):
/// a scope exposes its table; the host merges scopes along the focus chain
/// (see <see cref="BindingsChain"/>). <see cref="FocusableBase"/> deliberately
/// does NOT implement this — widgets adopt it one by one, and the router keeps
/// working on bare <see cref="IFocusable"/> until they do.
/// </summary>
public interface IBindingScope
{
    /// <summary>This scope's binding table.</summary>
    BindingsMap Bindings { get; }
}

/// <summary>
/// One scope's binding table (textual <c>BindingsMap</c> analogue): bindings
/// keyed by stable id, matched by gesture, overridden by id. Last bind wins
/// per id; same-gesture ties resolve by <see cref="Binding.Priority"/>.
/// Not thread-safe — render thread only, like <see cref="FocusStack"/>.
/// </summary>
public sealed class BindingsMap
{
    private readonly Dictionary<string, Binding> _byId = new(StringComparer.Ordinal);

    /// <summary>Bound binding count.</summary>
    public int Count => _byId.Count;

    /// <summary>Binds (or replaces) <paramref name="binding"/> by its id.</summary>
    public void Bind(Binding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.Action);
        _byId[binding.Id] = binding;
    }

    /// <summary>Removes the binding with <paramref name="id"/>. False when absent.</summary>
    public bool Unbind(string id) => _byId.Remove(id);

    /// <summary>Drops every binding.</summary>
    public void Clear() => _byId.Clear();

    /// <summary>Looks up a binding by id. False when absent.</summary>
    public bool TryGet(string id, out Binding? binding) => _byId.TryGetValue(id, out binding);

    /// <summary>
    /// Best-match lookup: every binding whose gesture fires on
    /// <paramref name="key"/> contends, highest <see cref="Binding.Priority"/>
    /// wins. Gesture match stays case-EXACT (see <see cref="KeyGesture"/>).
    /// </summary>
    public bool TryMatch(KeyEvent key, out Binding? hit)
    {
        hit = null;
        foreach (Binding candidate in _byId.Values)
        {
            if (!candidate.Gesture.Matches(key))
            {
                continue;
            }

            if (hit is null || candidate.Priority > hit.Priority)
            {
                hit = candidate;
            }
        }

        return hit is not null;
    }

    /// <summary>
    /// Merges <paramref name="focusToApp"/> (focused scope first, app map last)
    /// into one table: focused bindings win every gesture clash regardless of
    /// priority (the loser is dropped even when its id differs), so the merged
    /// map reads exactly like the chain resolves.
    /// </summary>
    public static BindingsMap Merge(IReadOnlyList<BindingsMap> focusToApp)
    {
        ArgumentNullException.ThrowIfNull(focusToApp);
        var merged = new BindingsMap();
        for (int i = focusToApp.Count - 1; i >= 0; i--)
        {
            BindingsMap scope = focusToApp[i];
            if (scope is null)
            {
                continue;
            }

            foreach (Binding binding in scope._byId.Values)
            {
                merged.RemoveByGesture(binding.Gesture);
                merged._byId[binding.Id] = binding;
            }
        }

        return merged;
    }

    /// <summary>
    /// User keymap override by id (textual <c>apply_keymap</c>): rebinds
    /// <paramref name="id"/> to <paramref name="spec"/>. The override applies
    /// even on clash — <paramref name="clashAction"/> names the displaced
    /// binding's action (null when clean) so the host can warn.
    /// </summary>
    /// <returns>False when <paramref name="id"/> is unknown or the spec is bad.</returns>
    public bool ApplyOverride(string id, string spec, out string? clashAction)
    {
        clashAction = null;
        if (!_byId.TryGetValue(id, out Binding? current))
        {
            return false;
        }

        if (!KeySpec.TryParse(spec, out KeyGesture gesture))
        {
            return false;
        }

        return ApplyOverride(id, gesture, out clashAction, current);
    }

    /// <summary>Gesture overload of <see cref="ApplyOverride(string, string, out string?)"/>.</summary>
    /// <returns>False when <paramref name="id"/> is unknown.</returns>
    public bool ApplyOverride(string id, KeyGesture gesture, out string? clashAction)
    {
        clashAction = null;
        if (!_byId.TryGetValue(id, out Binding? current))
        {
            return false;
        }

        return ApplyOverride(id, gesture, out clashAction, current);
    }

    private bool ApplyOverride(string id, KeyGesture gesture, out string? clashAction, Binding current)
    {
        clashAction = null;
        foreach (Binding other in _byId.Values)
        {
            if (!string.Equals(other.Id, id, StringComparison.Ordinal) && other.Gesture.Equals(gesture))
            {
                clashAction = other.Action;
                break;
            }
        }

        _byId[id] = current with { Gesture = gesture };
        return true;
    }

    /// <summary>
    /// Footer/help rows for free (textual <c>shown_keys</c>): visible bindings
    /// ordered by descending priority, ties by id. The caller owns the list.
    /// </summary>
    public List<Binding> ShownKeys()
    {
        var shown = new List<Binding>();
        foreach (Binding binding in _byId.Values)
        {
            if (binding.Show)
            {
                shown.Add(binding);
            }
        }

        shown.Sort(static (a, b) =>
        {
            int byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        return shown;
    }

    private void RemoveByGesture(KeyGesture gesture)
    {
        string? loser = null;
        foreach (Binding binding in _byId.Values)
        {
            if (binding.Gesture.Equals(gesture))
            {
                loser = binding.Id;
                break;
            }
        }

        if (loser is not null)
        {
            _ = _byId.Remove(loser);
        }
    }
}

/// <summary>
/// Focus-chain helpers: resolve one key without allocating a merged map
/// (focused scope first, app map last — first match wins), or build the merged
/// map once for footer/help rendering. Scopes that cannot focus are skipped,
/// mirroring the router's in-place skip.
/// </summary>
public static class BindingsChain
{
    /// <summary>
    /// Resolves <paramref name="key"/> along the chain: the focused entry's
    /// scope first, down the stack, <paramref name="app"/> last.
    /// </summary>
    /// <returns>False when no scope matched (host falls back to its keymap).</returns>
    public static bool TryResolve(KeyEvent key, FocusStack? stack, BindingsMap? app, out Binding? hit)
    {
        hit = null;
        if (stack is not null)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                IFocusable entry = stack[i];
                if (!entry.CanFocus || entry is not IBindingScope scope)
                {
                    continue;
                }

                if (scope.Bindings.TryMatch(key, out hit))
                {
                    return true;
                }
            }
        }

        if (app is not null && app.TryMatch(key, out hit))
        {
            return true;
        }

        hit = null;
        return false;
    }

    /// <summary>
    /// Builds the merged map for the same chain <see cref="TryResolve"/>
    /// walks (for footer/help). Focused scopes win gesture clashes.
    /// </summary>
    public static BindingsMap Merge(FocusStack? stack, BindingsMap? app)
    {
        var chain = new List<BindingsMap>();
        if (stack is not null)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                IFocusable entry = stack[i];
                if (!entry.CanFocus || entry is not IBindingScope scope)
                {
                    continue;
                }

                chain.Add(scope.Bindings);
            }
        }

        if (app is not null)
        {
            chain.Add(app);
        }

        return BindingsMap.Merge(chain);
    }
}
