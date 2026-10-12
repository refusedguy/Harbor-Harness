// TR4 (#1223, epic #1195): modal focus stack over IFocusable —
// Push, Pop and MoveToTop with blur-then-focus notifications, CanFocus-skipping,
// render-thread owned. The SIBLING of FocusRouter, not its replacement:
// FocusRouter is the flat Tab-order list (lazygit style with wrap-around),
// this is the overlay/modal stack (Termina focus-stack analogue: a pushed
// dialog owns keys until popped). A host may mirror Current into the router
// via FocusById — deliberately manual, so neither type depends on the other.
// ConsoleEx FocusManager lends the gate (unfocusable entries never own
// focus); its per-window threads are an explicit non-goal, not taken.
// Same leaf rule: BCL-only, AOT-compatible, zero Harbor references, chat untouched.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Last-pushed-owns-focus stack. Push blurs the old top and focuses the new
/// one; Pop blurs the top and focuses the entry beneath. Entries with
/// <see cref="IFocusable.CanFocus"/> false are refused at Push and skipped by
/// <see cref="TryFocusById"/> — a disabled control keeps no seat here
/// (unlike the bubble walk, which skips dynamically-flipped entries in place).
/// Not thread-safe — render thread only, same as <see cref="FocusRouter"/>.
/// </summary>
public sealed class FocusStack
{
    private readonly List<IFocusable> _order = [];

    /// <summary>Focused entry (stack top), or null when the stack is empty.</summary>
    public IFocusable? Current => _order.Count == 0 ? null : _order[^1];

    /// <summary>Stack depth.</summary>
    public int Count => _order.Count;

    /// <summary>Indexed oldest-to-newest for the router's allocation-free walks.</summary>
    public IFocusable this[int index] => _order[index];

    /// <summary>Whether the stack holds no entries.</summary>
    public bool IsEmpty => _order.Count == 0;

    /// <summary>
    /// Pushes an entry and focuses it. Refused (false, stack untouched) when
    /// <paramref name="target"/> is null, cannot focus, or is already the top.
    /// Pushing an entry that sits deeper re-seats it on top (dialog re-raised).
    /// </summary>
    public bool Push(IFocusable? target)
    {
        if (target is null || !target.CanFocus)
        {
            return false;
        }

        if (Current is not null && ReferenceEquals(Current, target))
        {
            return true;
        }

        _order.RemoveAll(e => ReferenceEquals(e, target));
        Current?.OnFocusChanged(false);
        _order.Add(target);
        target.OnFocusChanged(true);
        return true;
    }

    /// <summary>
    /// Pops the top entry and focuses the one beneath. False when empty;
    /// popping to empty leaves no focused entry (no null-focus callback —
    /// the popped entry already got its blur).
    /// </summary>
    public bool Pop()
    {
        if (_order.Count == 0)
        {
            return false;
        }

        IFocusable top = _order[^1];
        _order.RemoveAt(_order.Count - 1);
        top.OnFocusChanged(false);
        Current?.OnFocusChanged(true);
        return true;
    }

    /// <summary>
    /// Mouse-click routing analogue of <see cref="FocusRouter.FocusById"/>:
    /// re-seats the named entry on top and focuses it. False when the id is
    /// unknown or the entry currently cannot focus (stack untouched).
    /// </summary>
    public bool TryFocusById(string id)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id)
            {
                return Push(_order[i]);
            }
        }

        return false;
    }

    /// <summary>Whether the named id sits anywhere on the stack.</summary>
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

    /// <summary>
    /// Removes the named entry wherever it sits. Removing the top focuses the
    /// entry beneath; removing a buried entry notifies nobody. False when
    /// the id is unknown.
    /// </summary>
    public bool Remove(string id)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id)
            {
                bool wasTop = i == _order.Count - 1;
                IFocusable removed = _order[i];
                _order.RemoveAt(i);
                if (wasTop)
                {
                    removed.OnFocusChanged(false);
                    Current?.OnFocusChanged(true);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Blurs the top (when any) and drops every entry.</summary>
    public void Clear()
    {
        Current?.OnFocusChanged(false);
        _order.Clear();
    }
}
