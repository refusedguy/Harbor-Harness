// #1199 (epic #1195): focus scopes + modal trap —
// FocusScope (container = router: tab container, form, modal trap) +
// FocusScopeStack (scope stack with focus return on modal close) +
// globals-up/rest-down-to-active-scope routing convention.
// Ports: input-form main.rs:101-131 (Focus enum + next() + match delegation,
// cursor as derived) and demo2 app.rs handle_events (global keys on App,
// rest match active tab). Siblings, not replacements: FocusRouter stays the
// flat Tab-order list, FocusStack stays the entry-level modal stack; a scope
// OWNS one tab-order over its entries and the stack owns which scope is
// active. Modals (DialogOverlayLayer, JumpPaletteOverlayLayer, approval-gate)
// become scopes instead of ad-hoc router bypasses.
// Same leaf rule: BCL-only, AOT-compatible, zero Harbor references,
// render-thread owned, chat untouched.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Scope flavor. Routing treats every scope as a trap (only the active scope
/// sees non-global keys); the flavor tells the host how to paint and close it:
/// <see cref="FocusScopeKind.Container"/> is a tab page,
/// <see cref="FocusScopeKind.Form"/> is a multi-field group with its own Tab
/// order, <see cref="FocusScopeKind.Modal"/> additionally paints the overlay
/// barrier and closes on Escape.
/// </summary>
public enum FocusScopeKind : byte
{
    /// <summary>Tab container / page.</summary>
    Container = 0,

    /// <summary>Multi-field form with its own Tab order.</summary>
    Form = 1,

    /// <summary>Modal trap: overlay barrier + Escape-to-close.</summary>
    Modal = 2,
}

/// <summary>
/// Named focus container: one Tab-order over <see cref="IFocusable"/> entries
/// (input-form <c>Focus::next()</c> analogue — Tab/Shift+Tab wrap, direct jump
/// by id). Cursor/focus is derived (<see cref="Current"/>), never stored
/// beside the order. Not thread-safe — render thread only.
/// </summary>
public sealed class FocusScope
{
    private readonly List<IFocusable> _order = [];
    private int _index = -1;

    /// <summary>Creates a scope with the given stable id and flavor.</summary>
    public FocusScope(string id, FocusScopeKind kind = FocusScopeKind.Container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Kind = kind;
    }

    /// <summary>Stable scope id (tab id, form id, modal id).</summary>
    public string Id { get; }

    /// <summary>Scope flavor (container, form, modal trap).</summary>
    public FocusScopeKind Kind { get; }

    /// <summary>Whether this scope is a modal trap.</summary>
    public bool IsModal => Kind == FocusScopeKind.Modal;

    /// <summary>Focused entry, or null when the scope holds no focused entry.</summary>
    public IFocusable? Current => _index >= 0 && _index < _order.Count ? _order[_index] : null;

    /// <summary>Entry count.</summary>
    public int Count => _order.Count;

    /// <summary>Indexed oldest-to-newest for the router's allocation-free walks.</summary>
    public IFocusable this[int index] => _order[index];

    /// <summary>Adds an entry to the scope's Tab order.</summary>
    public void Add(IFocusable target) => _order.Add(target);

    /// <summary>Whether the named entry sits in this scope.</summary>
    public bool Contains(string id)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Advances focus (Tab, wraps around, skips entries with CanFocus false).</summary>
    public bool Next()
    {
        if (_order.Count == 0)
        {
            return false;
        }

        for (int step = 1; step <= _order.Count; step++)
        {
            int candidate = (_index + step + _order.Count) % _order.Count;
            if (_order[candidate].CanFocus)
            {
                return SetIndex(candidate);
            }
        }

        return false;
    }

    /// <summary>Moves focus back (Shift+Tab, wraps around). From an unfocused
    /// state lands on the last focusable entry.</summary>
    public bool Previous()
    {
        if (_order.Count == 0)
        {
            return false;
        }

        int start = _index < 0 ? _order.Count : _index;
        for (int step = 1; step <= _order.Count; step++)
        {
            int candidate = (start - step + _order.Count) % _order.Count;
            if (_order[candidate].CanFocus)
            {
                return SetIndex(candidate);
            }
        }

        return false;
    }

    /// <summary>Focuses the named entry. False when unknown or not focusable.</summary>
    public bool FocusById(string id)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id)
            {
                return _order[i].CanFocus && SetIndex(i);
            }
        }

        return false;
    }

    /// <summary>Seeds focus per policy (Termina ChatPage.FocusPolicy analogue).
    /// <see cref="FocusPolicy.None"/> leaves focus untouched.</summary>
    public bool Activate(FocusPolicy policy)
    {
        if (policy == FocusPolicy.FirstFocusable)
        {
            int saved = _index;
            _index = -1;
            if (Next())
            {
                return true;
            }

            _index = saved;
            return false;
        }

        if (policy == FocusPolicy.LastFocusable)
        {
            int saved = _index;
            _index = _order.Count;
            bool found = Previous();
            if (!found)
            {
                _index = saved;
            }

            return found;
        }

        return Current is not null;
    }

    internal bool SetIndex(int index)
    {
        if (index == _index && Current is not null)
        {
            return true;
        }

        var old = Current;
        old?.OnFocusChanged(false);
        _index = index;
        Current?.OnFocusChanged(true);
        return true;
    }

    internal void Blur()
    {
        Current?.OnFocusChanged(false);
        _index = -1;
    }
}

/// <summary>
/// Scope stack with focus return (demo2 <c>App.active_tab</c> analogue, one
/// level stricter): Push blurs the active scope's entry and remembers it;
/// Pop drops the top scope and re-focuses the remembered entry beneath —
/// closing a modal returns focus where it was, the host keeps no bookmark.
/// Only the active scope participates in routing (globals-up / rest-down):
/// a pushed modal owns keys because everything beneath simply is not walked
/// (the <c>OverlayLayer</c> modal barrier is the paint sibling, this is the
/// key sibling). Not thread-safe — render thread only.
/// </summary>
public sealed class FocusScopeStack
{
    private readonly List<FocusScope> _scopes = [];
    private readonly List<(string ScopeId, string? EntryId)> _return = [];

    /// <summary>Active scope (stack top), or null when empty.</summary>
    public FocusScope? Active => _scopes.Count == 0 ? null : _scopes[^1];

    /// <summary>Scope depth.</summary>
    public int Count => _scopes.Count;

    /// <summary>Whether the stack holds no scopes.</summary>
    public bool IsEmpty => _scopes.Count == 0;

    /// <summary>
    /// Pushes a scope and focuses its seed entry. The previously active entry
    /// (when any) is blurred and remembered for <see cref="Pop"/>.
    /// Pushing a scope that is already on the stack is a no-op (true) — the
    /// return records stay LIFO-parallel with the scopes, so neither type
    /// re-seats here (unlike <see cref="FocusStack.Push"/>, whose entries
    /// carry no return records). Refused (false, stack untouched) only when
    /// <paramref name="scope"/> is null.
    /// </summary>
    public bool Push(FocusScope? scope, FocusPolicy seed = FocusPolicy.FirstFocusable)
    {
        if (scope is null)
        {
            return false;
        }

        for (int i = 0; i < _scopes.Count; i++)
        {
            if (ReferenceEquals(_scopes[i], scope))
            {
                return true;
            }
        }

        FocusScope? prev = Active;
        string? returnEntry = prev?.Current?.Id;
        prev?.Blur();
        _return.Add((prev?.Id ?? string.Empty, returnEntry));
        _scopes.Add(scope);
        scope.Activate(seed);
        return true;
    }

    /// <summary>
    /// Pops the active scope and returns focus to the entry remembered at
    /// <see cref="Push"/> time. False when empty. Popping to empty leaves no
    /// focused entry.
    /// </summary>
    public bool Pop()
    {
        if (_scopes.Count == 0)
        {
            return false;
        }

        FocusScope top = _scopes[^1];
        _scopes.RemoveAt(_scopes.Count - 1);
        top.Blur();
        (string scopeId, string? entryId) = _return[^1];
        _return.RemoveAt(_return.Count - 1);

        FocusScope? beneath = Active;
        if (beneath is not null)
        {
            if (entryId is null
                || beneath.Id != scopeId
                || !beneath.FocusById(entryId))
            {
                beneath.Activate(FocusPolicy.FirstFocusable);
            }
        }

        return true;
    }

    /// <summary>Whether the named scope sits anywhere on the stack.</summary>
    public bool Contains(string id)
    {
        for (int i = 0; i < _scopes.Count; i++)
        {
            if (_scopes[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Blurs the active entry (when any) and drops every scope.</summary>
    public void Clear()
    {
        Active?.Blur();
        _scopes.Clear();
        _return.Clear();
    }

    /// <summary>
    /// Routes one key: globals-up (capture table first — a hit reports
    /// <see cref="InputDisposition.CaptureHandled"/> and no scope is
    /// consulted), rest-down-to-active-scope (preview tunnel bottom-up then
    /// bubble top-down over the ACTIVE scope only; beneath scopes never see
    /// the key — that is the modal trap). Remainder runs when nothing
    /// consumed the key.
    /// </summary>
    public InputDisposition Route(
        KeyEvent key,
        KeyBindings? globals,
        out string? actionId,
        Action<KeyEvent>? remainder = null)
    {
        if (globals is not null && globals.TryMatch(key, out actionId))
        {
            return InputDisposition.CaptureHandled;
        }

        actionId = null;
        FocusScope? active = Active;
        if (active is not null)
        {
            for (int i = 0; i < active.Count; i++)
            {
                IFocusable entry = active[i];
                if (entry.CanFocus && entry.PreviewKey(key))
                {
                    return InputDisposition.PreviewHandled;
                }
            }

            for (int i = active.Count - 1; i >= 0; i--)
            {
                IFocusable entry = active[i];
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
