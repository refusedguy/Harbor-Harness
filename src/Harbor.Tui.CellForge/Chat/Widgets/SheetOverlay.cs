// #2376: the Sheet primitive from gpui-kit-components.md. The port is the
// CONTRACT of an edge-anchored panel with controlled open state, a focus trap
// and keyboard dismissal — not GPUI's animation runtime.
//
// State stays outside the painter: the host sets IsOpen and Progress, and the
// layer seated on OverlayStack derives its box from them. Modality is not
// re-invented here: SheetOverlayLayer declares IOverlayLayer.IsModal through
// OverlayLayerBase, so the existing OverlayStack barrier and the host's key
// cascade do the excluding. Focus traversal is the engine FocusRouter's flat
// Tab order, whose Next/Previous wrap is the trap — no second router is added.

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Edge a <see cref="SheetOverlay"/> slides in from.</summary>
public enum SheetEdge : byte
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// One focusable row inside a <see cref="SheetOverlay"/>. Implements the engine
/// focus contract, so the sheet's <see cref="Harbor.Tui.CellForge.Input.FocusRouter"/>
/// Tab order owns it without a renderer-specific target type.
/// </summary>
public sealed class SheetFocusItem : Harbor.Tui.CellForge.Input.IFocusTarget
{
    public SheetFocusItem(string id, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    public string Id { get; }

    public string Label { get; }

    /// <summary>Set by the router when this row enters or leaves focus.</summary>
    public bool IsFocused { get; private set; }

    /// <inheritdoc />
    public void OnFocusChanged(bool focused) => IsFocused = focused;
}

/// <summary>
/// Controlled sheet state: an open flag, an edge, a slide progress and the rows
/// to paint. The host owns every value (there is no timer inside this type);
/// <see cref="SheetOverlayLayer"/> reads them once per frame. Escape closes the
/// sheet and Tab / Shift+Tab move focus inside the router's wrapping order.
/// </summary>
public sealed class SheetOverlay
{
    private bool _isOpen;

    /// <summary>
    /// External open state. A false→true transition seeds focus on the first
    /// item; a true→false transition clears it, which is the focus-return half
    /// of the trap contract.
    /// </summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
            {
                return;
            }

            _isOpen = value;
            if (value)
            {
                Focus.Jump(0);
            }
            else
            {
                Focus.Clear();
            }
        }
    }

    /// <summary>Which edge the panel is anchored to. Defaults to the right.</summary>
    public SheetEdge Edge { get; set; } = SheetEdge.Right;

    /// <summary>
    /// Slide position: 0 is fully off the edge, 1 is fully in. The host drives
    /// this value; the layer only turns it into geometry.
    /// </summary>
    public double Progress { get; set; } = 1.0;

    /// <summary>Width (left/right) or height (top/bottom) of the fully-in sheet.</summary>
    public int Extent { get; set; } = 32;

    /// <summary>Header text painted on the first interior row.</summary>
    public string Title { get; set; } = "Sheet";

    /// <summary>Focusable rows, in Tab order. Replaced as a group by <see cref="SetItems"/>.</summary>
    public IReadOnlyList<SheetFocusItem> Items { get; private set; } = Array.Empty<SheetFocusItem>();

    /// <summary>Body lines painted below the focus rows.</summary>
    public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

    /// <summary>The sheet's own Tab order; wrapping Next/Previous is the focus trap.</summary>
    public Harbor.Tui.CellForge.Input.FocusRouter Focus { get; } = new();

    /// <summary>Replaces the focus rows and rebuilds the router from them.</summary>
    public void SetItems(params SheetFocusItem[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Focus.Clear();
        var copy = new SheetFocusItem[items.Length];
        Array.Copy(items, copy, items.Length);
        Items = copy;
        for (int i = 0; i < copy.Length; i++)
        {
            Focus.Add(copy[i]);
        }

        if (_isOpen && copy.Length > 0)
        {
            Focus.Jump(0);
        }
    }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    /// <summary>
    /// Offers a decoded key to the open sheet. Escape dismisses it; Tab and
    /// Shift+Tab wrap focus within the sheet's own router. Every other key is
    /// consumed while the sheet is up, so nothing reaches the panels beneath.
    /// Returns false when the sheet is closed.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!_isOpen)
        {
            return false;
        }

        if (key.Key == KeyCode.Escape)
        {
            Close();
            return true;
        }

        if (key.Key == KeyCode.Tab)
        {
            if ((key.Modifiers & KeyModifiers.Shift) != KeyModifiers.None)
            {
                Focus.Previous();
            }
            else
            {
                Focus.Next();
            }

            return true;
        }

        return true;
    }
}
