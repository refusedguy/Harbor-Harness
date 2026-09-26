using System.Text;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels;
/// <summary>
///     Dynamic Spectre <see cref="Layout" /> tree built from the current
///     <see cref="PanelRegistryView" /> snapshot. Replaces the static
///     <c>ChatLayoutShell</c> — the chat chrome (Header/History/StreamBar/Input/Footer)
///     is now the centre column, surrounded by optional Left/Right/Top/Bottom panel
///     columns/rows. Panels that share a placement are stacked and a 1-row tab strip
///     is rendered above them.
/// </summary>
/// <remarks>
///     <para>
///         <b>TEA compliance:</b> all visibility / size reads come from the supplied
///         <see cref="UiState" /> (via <see cref="PanelRegistryView" />). The layout
///         shell never mutates state — it only builds a Spectre <see cref="Layout" />
///         tree from a snapshot. State transitions happen in <see cref="UiReducer" />.
///     </para>
///     <para>
///         <b>Layout shape (no panels):</b> identical to the old <c>ChatLayoutShell</c>
///         so users without registered panels see no change.
///     </para>
///     <para>
///         <b>Layout shape (Left + Bottom panels visible):</b>
///     </para>
///     <code>
/// Root (rows)
/// ├── Header                       (1 row)
/// ├── Body (cols)
/// │   ├── Left                     (tabs + content)
/// │   ├── Center (rows)
/// │   │   ├── History              (flex)
/// │   │   ├── StreamBar            (1 row, only when streaming)
/// │   │   └── Bottom               (tabs + content)
/// │   └── Right                    (tabs + content, hidden when no right panels)
/// ├── Input                        (3 rows)
/// └── Footer                       (1 row)
///     </code>
///     <para>
///         The tree is rebuilt only when the visible-set / size / streaming flag
///         changes — same optimisation as the original <c>ChatLayoutShell.Ensure</c>.
///     </para>
/// </remarks>
internal sealed class PanelLayoutShell
{
    private readonly PanelRegistry _registry;
    private string _signature = string.Empty;

    public PanelLayoutShell(PanelRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Current Spectre layout tree. Replace on signature change.</summary>
    public Layout Layout
    {
        get;
        private set;
    } = Empty();

    /// <summary>
    ///     Rebuild the layout if the visible-set / size / streaming signature has
    ///     changed since the last call. Cheap no-op otherwise.
    /// </summary>
    /// <param name="state">Current immutable UI snapshot — source of all panel state.</param>
    /// <param name="streaming">Whether the StreamBar row should be present.</param>
    public void Ensure(UiState state, bool streaming)
    {
        string sig = BuildSignature(state, streaming);
        if (sig == _signature)
            return;

        _signature = sig;
        Layout = BuildLayout(state, streaming);
    }

    /// <summary>
    ///     Compute a hashable signature string from the current registry view so
    ///     <see cref="Ensure" /> can skip rebuilds when nothing has changed.
    /// </summary>
    private string BuildSignature(UiState state, bool streaming)
    {
        var view = _registry.View(state);
        var sb = new StringBuilder(64);
        sb.Append(streaming ? "S1|" : "S0|");
        foreach (var p in view.Providers)
        {
            var panelState = view.GetState(p.Id);
            if (panelState == TuiPanelState.Hidden)
                continue;
            int size = view.GetSize(p.Id);
            sb.Append(p.Id).Append(':')
                .Append((int)p.DefaultPlacement).Append(':')
                .Append((int)panelState).Append(':')
                .Append(size).Append('|');
        }
        return sb.ToString();
    }

    private Layout BuildLayout(UiState state, bool streaming)
    {
        var view = _registry.View(state);
        var left = view.GetVisibleByPlacement(TuiPanelPlacement.Left);
        var right = view.GetVisibleByPlacement(TuiPanelPlacement.Right);
        var top = view.GetVisibleByPlacement(TuiPanelPlacement.Top);
        var bottom = view.GetVisibleByPlacement(TuiPanelPlacement.Bottom);

        // ── No panels at all → identical to the original ChatLayoutShell.
        if (left.Count == 0 && right.Count == 0 && top.Count == 0 && bottom.Count == 0)
        {
            return streaming
                ? new Layout("Root").SplitRows(
                    new Layout("Header").Size(1),
                    new Layout("History"),
                    new Layout("StreamBar").Size(1),
                    new Layout("Input").Size(3),
                    new Layout("Footer").Size(1))
                : new Layout("Root").SplitRows(
                    new Layout("Header").Size(1),
                    new Layout("History"),
                    new Layout("Input").Size(3),
                    new Layout("Footer").Size(1));
        }

        // ── Build the centre column (chat chrome + Top above + Bottom below).
        var centreRows = new List<Layout>();

        // Top panels: 1 row of tabs + N rows of content (one panel stacked).
        if (top.Count > 0)
            centreRows.Add(BuildStack("TopPanels", top, TuiPanelPlacement.Top, view));

        centreRows.Add(new Layout("Header").Size(1));
        centreRows.Add(new Layout("History"));
        if (streaming)
            centreRows.Add(new Layout("StreamBar").Size(1));

        // Bottom panels: above the input box.
        if (bottom.Count > 0)
            centreRows.Add(BuildStack("BottomPanels", bottom, TuiPanelPlacement.Bottom, view));

        centreRows.Add(new Layout("Input").Size(3));
        centreRows.Add(new Layout("Footer").Size(1));

        var centre = new Layout("Centre").SplitRows(centreRows.ToArray());

        // ── No side panels → just rows.
        if (left.Count == 0 && right.Count == 0)
            return new Layout("Root").SplitRows(centreRows.ToArray());

        // ── Compose side columns.
        var cols = new List<Layout>();
        if (left.Count > 0)
            cols.Add(BuildStack("LeftPanels", left, TuiPanelPlacement.Left, view));
        cols.Add(centre);
        if (right.Count > 0)
            cols.Add(BuildStack("RightPanels", right, TuiPanelPlacement.Right, view));

        var body = new Layout("Body").SplitColumns(cols.ToArray());
        // When side panels exist, wrap Header/Footer around the body.
        return new Layout("Root").SplitRows(
            new Layout("Header").Size(1),
            body,
            new Layout("Input").Size(3),
            new Layout("Footer").Size(1));
    }

    /// <summary>
    ///     Build a stacked layout: 1-row tab strip on top, content below. For Left/Right
    ///     placements the stack is horizontal (tabs on the left column, content on right).
    /// </summary>
    private Layout BuildStack(
        string name,
        IReadOnlyList<IPanelProvider> panels,
        TuiPanelPlacement placement,
        PanelRegistryView view)
    {
        // Pick the first visible panel as the "active" tab content. Tabs themselves
        // are rendered into the same widget (the active panel's Build() also emits
        // the tab strip). For simplicity we show all visible panels of a placement
        // stacked — if multiple are visible, they share the space proportionally.
        var rows = new List<Layout>(panels.Count + 1);
        // Tab strip — 1 row (or 1 col for side placement).
        rows.Add(new Layout(name + ".Tabs").Size(1));
        // One region per visible panel.
#pragma warning disable S3267 // Hot path — explicit loop preferred over LINQ for performance.
        foreach (var p in panels)
        {
            int size = view.GetSize(p.Id);
            // Fixed size if explicit; otherwise unbounded (flex).
            var region = size > 0
                ? new Layout(name + "." + p.Id).Size(size)
                : new Layout(name + "." + p.Id);
            rows.Add(region);
        }
#pragma warning restore S3267
        return placement is TuiPanelPlacement.Left or TuiPanelPlacement.Right
            ? new Layout(name).SplitColumns(rows.ToArray())
            : new Layout(name).SplitRows(rows.ToArray());
    }

    private static Layout Empty() =>
        new Layout("Root").SplitRows(
            new Layout("Header").Size(1),
            new Layout("History"),
            new Layout("Input").Size(3),
            new Layout("Footer").Size(1));
}
