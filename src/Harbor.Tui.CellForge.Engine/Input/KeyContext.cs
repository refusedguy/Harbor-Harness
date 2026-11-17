// #2359: declarative key contexts — the binding half of the unified key
// dispatch tree (GPUI `.key_context("menu")` / `keymap.rs` analogue). BCL-only,
// AOT-compatible, zero Harbor references, render-thread owned — same leaf rule
// as KeyBindings.cs and BindingsMap.cs.
//
// This is NOT a second input router and NOT a second key→action vocabulary:
// bindings carry opaque action ids, so ChatKeyMap keeps owning the host's
// action names and LeaderKeyRouter keeps owning the chord vocabulary.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// One binding inside a <see cref="KeyContext"/>: a gesture mapped to an opaque
/// action id, or to an explicit no-action that consumes the key without firing
/// anything (the declarative twin of <c>ChatAction.None</c>, kept opaque here).
/// </summary>
public readonly record struct ContextBinding(KeyGesture Gesture, string? ActionId)
{
    /// <summary>True when this binding deliberately swallows the key.</summary>
    public bool IsNoAction => ActionId is null;

    /// <summary>Binds a gesture to an action id.</summary>
    public static ContextBinding Action(KeyGesture gesture, string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return new(gesture, actionId);
    }

    /// <summary>Binds a gesture to an explicit no-action.</summary>
    public static ContextBinding NoAction(KeyGesture gesture) => new(gesture, null);
}

/// <summary>
/// A named region of the view tree with its own key bindings. An attached
/// context overrides the contexts above it on the focus path; inside one
/// context the most recently bound gesture wins (insertion-order priority), so
/// a rebind is a re-seat, not a duplicate.
/// </summary>
public sealed class KeyContext
{
    private readonly List<ContextBinding> _bindings = [];

    /// <summary>Creates a named context.</summary>
    public KeyContext(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Stable context name (diagnostics / which-key grouping).</summary>
    public string Name { get; }

    /// <summary>Bound gesture count.</summary>
    public int Count => _bindings.Count;

    /// <summary>The bindings in insertion order (footer/help callers own the list).</summary>
    public IReadOnlyList<ContextBinding> Bindings => _bindings;

    /// <summary>Binds <paramref name="gesture"/> to <paramref name="actionId"/> (replaces).</summary>
    public void Bind(KeyGesture gesture, string actionId) => Set(ContextBinding.Action(gesture, actionId));

    /// <summary>Binds <paramref name="gesture"/> to an explicit no-action (replaces).</summary>
    public void BindNoAction(KeyGesture gesture) => Set(ContextBinding.NoAction(gesture));

    /// <summary>Removes the binding for <paramref name="gesture"/>. False when absent.</summary>
    public bool Unbind(KeyGesture gesture)
    {
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            if (_bindings[i].Gesture.Equals(gesture))
            {
                _bindings.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops every binding.</summary>
    public void Clear() => _bindings.Clear();

    /// <summary>
    /// Best match for <paramref name="key"/>: the most recently bound matching
    /// gesture wins, so insertion order breaks ties inside one context.
    /// </summary>
    public bool TryMatch(KeyEvent key, out ContextBinding hit)
    {
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            if (_bindings[i].Gesture.Matches(key))
            {
                hit = _bindings[i];
                return true;
            }
        }

        hit = default;
        return false;
    }

    private void Set(ContextBinding binding)
    {
        // Re-seat last so the rebind wins the next equal-gesture tie (same rule
        // as BindingsMap.Bind). Without this a rebound gesture would keep the
        // old entry's slot and lose to a later binding it should override.
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            if (_bindings[i].Gesture.Equals(binding.Gesture))
            {
                _bindings.RemoveAt(i);
                break;
            }
        }

        _bindings.Add(binding);
    }
}
