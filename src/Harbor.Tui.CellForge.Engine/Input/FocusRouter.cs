namespace Harbor.Tui.CellForge.Input;

// IFocusTarget moved to Harbor.Ui.Framework.Rendering.Input (renderer-agnostic
// shared vocabulary); FocusRouter keeps consuming it via GlobalUsings.

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
