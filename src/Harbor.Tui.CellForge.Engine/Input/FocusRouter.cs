
namespace Harbor.Tui.CellForge.Input;

// IFocusTarget is engine-owned (Harbor.Tui.CellForge.Input, #436): the focus
// vocabulary lives in the standalone leaf now, so this file spells no borrow.

/// <summary>
/// Flat Tab-order focus traversal (lazygit style): Tab/Shift+Tab wrap around,
/// Alt+1..9-style direct jumps by index, mouse clicks route through
/// <see cref="FocusById"/>.
/// </summary>
public sealed class FocusRouter
{
    private readonly List<IFocusTarget> _order = [];
    private int _index = -1;

    public IFocusTarget? Current => _index >= 0 && _index < _order.Count ? _order[_index] : null;

    public IReadOnlyList<IFocusTarget> Order => _order;

    public void Add(IFocusTarget target) => _order.Add(target);

    /// <summary>
    /// Removes the named entry wherever it sits. Removing the focused entry
    /// blurs it and leaves the router unfocused (caller routes explicitly via
    /// Next/Previous/FocusById); removing an entry before the focused one
    /// shifts the index so Current stays put. False when the id is unknown.
    /// </summary>
    public bool Remove(string id)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id)
            {
                bool wasCurrent = i == _index;
                IFocusTarget removed = _order[i];
                _order.RemoveAt(i);
                if (wasCurrent)
                {
                    removed.OnFocusChanged(false);
                    _index = -1;
                }
                else if (i < _index)
                {
                    _index--;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Blurs the focused entry (when any) and drops every entry.</summary>
    public void Clear()
    {
        Current?.OnFocusChanged(false);
        _order.Clear();
        _index = -1;
    }

    public bool Next()
    {
        if (_order.Count == 0)
        {
            return false;
        }

        return SetIndex((_index + 1) % _order.Count);
    }

    public bool Previous()
    {
        if (_order.Count == 0)
        {
            return false;
        }

        // From an unfocused state, Shift+Tab lands on the last target.
        if (_index < 0)
        {
            return SetIndex(_order.Count - 1);
        }

        return SetIndex((_index - 1 + _order.Count) % _order.Count);
    }

    public bool Jump(int index)
    {
        if (index < 0 || index >= _order.Count)
        {
            return false;
        }

        return SetIndex(index);
    }

    public bool FocusById(string id)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id)
            {
                return SetIndex(i);
            }
        }

        return false;
    }

    private bool SetIndex(int index)
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
}
