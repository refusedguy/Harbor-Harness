using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>
/// One generic composition node (epic #1195, part of #1226): data for a single
/// <see cref="LayoutTree"/> leaf with no chat coupling. The chat factory takes
/// a composer plus a status view-model; this one takes an id, minimums and
/// neutral content (a form field or static lines) — the anti-pattern the issue
/// names, fixed by construction.
/// </summary>
public sealed class GenericNodeSpec
{
    /// <summary>Layout-tree id (must be unique within the screen).</summary>
    public required string Id { get; init; }

    /// <summary>Title painted on the first row (empty draws no title).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Solver minimum width.</summary>
    public int MinWidth { get; init; } = 10;

    /// <summary>Solver minimum height.</summary>
    public int MinHeight { get; init; } = 1;

    /// <summary>Collapse priority (lower collapses first; status-like rows use large values).</summary>
    public int Priority { get; init; }

    /// <summary>Interactive content (optional; static <see cref="Lines"/> when null).</summary>
    public IGenericFormField? Field { get; init; }

    /// <summary>Static lines painted under the title (ignored when <see cref="Field"/> is set).</summary>
    public IReadOnlyList<string>? Lines { get; init; }
}

/// <summary>
/// One generic split (epic #1195, part of #1226): retargets the leaf named by
/// <see cref="TargetId"/> the way <see cref="LayoutTree.Split"/> does, seating
/// <see cref="NewPanelId"/> beside it. Both ids must name entries of the node
/// list passed to <see cref="GenericScreen.Build"/>.
/// </summary>
public sealed class GenericSplitSpec
{
    /// <summary>Leaf to split (must already be seated).</summary>
    public required string TargetId { get; init; }

    /// <summary>Split axis.</summary>
    public SplitDir Direction { get; init; } = SplitDir.Vertical;

    /// <summary>Share of the usable extent kept by the target side.</summary>
    public float Ratio { get; init; } = 0.5f;

    /// <summary>Node to seat beside the target (must name a node entry).</summary>
    public required string NewPanelId { get; init; }

    /// <summary>Gap cells between the sides.</summary>
    public byte Gap { get; init; }
}

/// <summary>
/// Leaf panel hosting one <see cref="GenericNodeSpec"/>. Paints the title row
/// plus static lines, or delegates the content rect to the hosted form field.
/// Focus state is host-driven (see <see cref="GenericScreen"/>); the panel only
/// reads it for the title marker.
/// </summary>
public sealed class GenericContentPanel : Panel
{
    private static readonly CellStyle TitleStyle = new(ChatPalette.Accent, attrs: StyleAttr.Bold);
    private static readonly CellStyle BodyStyle = new(ChatPalette.Text);
    private static readonly CellStyle DimStyle = ChatPalette.Dim;

    public GenericContentPanel(GenericNodeSpec spec)
        : base(spec.Id, new Size(Math.Max(1, spec.MinWidth), Math.Max(1, spec.MinHeight)), spec.Priority)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Title = spec.Title ?? string.Empty;
        Field = spec.Field;
        Lines = spec.Lines ?? Array.Empty<string>();
    }

    /// <summary>Title painted on the first row.</summary>
    public string Title { get; }

    /// <summary>Interactive content (null = static lines).</summary>
    public IGenericFormField? Field { get; }

    /// <summary>Static lines (empty when a field is hosted).</summary>
    public IReadOnlyList<string> Lines { get; }

    public override void Paint(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (Rect.Width <= 0 || Rect.Height <= 0)
        {
            return;
        }

        buffer.Fill(Rect, Cell.Blank);

        int y = Rect.Y;
        int bottom = Rect.Y + Rect.Height;
        if (Title.Length > 0 && y < bottom)
        {
            string marker = Focused ? "› " : string.Empty;
            string head = marker + Title;
            buffer.SetText(Rect.X, y, head.AsSpan(0, Math.Min(head.Length, Rect.Width)), Focused ? TitleStyle : DimStyle);
            y++;
        }

        if (Field is not null)
        {
            int h = Math.Max(0, bottom - y);
            if (h > 0)
            {
                Field.Paint(buffer, new Rect(Rect.X, y, Rect.Width, h));
            }

            return;
        }

        for (int i = 0; i < Lines.Count && y < bottom; i++, y++)
        {
            string line = Lines[i] ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            buffer.SetText(Rect.X, y, line.AsSpan(0, Math.Min(line.Length, Rect.Width)), BodyStyle);
        }
    }
}

/// <summary>
/// Assembled generic screen (epic #1195, part of #1226): a <see cref="LayoutTree"/>
/// built from <see cref="GenericNodeSpec"/> plus <see cref="GenericSplitSpec"/>,
/// run on the generic TEA half (<c>UiState.Ui</c>) with zero chat knowledge.
/// The chat screen stays alongside untouched; this is the neutral composition
/// future dialogs, palettes and wizards build from.
/// </summary>
public sealed class GenericScreen
{
    private readonly Dictionary<string, GenericContentPanel> _panels = new(StringComparer.Ordinal);
    private readonly List<string> _focusOrder = [];

    private GenericScreen(LayoutTree tree)
    {
        Tree = tree;
    }

    /// <summary>Assembled layout tree (solved per frame via <see cref="Solve"/>).</summary>
    public LayoutTree Tree { get; }

    /// <summary>Panel count.</summary>
    public int Count => _panels.Count;

    /// <summary>Focus order (node order).</summary>
    public IReadOnlyList<string> FocusOrder => _focusOrder;

    /// <summary>Focused panel id (null when empty).</summary>
    public string? FocusedId { get; private set; }

    /// <summary>
    /// Builds the screen from nodes plus splits. The first node is the root;
    /// each split seats one more node beside its target. Pure setup-time work
    /// (no buffer writes, no store access) so tests can assert the tree shape
    /// without a terminal.
    /// </summary>
    /// <param name="nodes">Leaves in seating order (at least one).</param>
    /// <param name="splits">Splits seating nodes 1..N (one per extra node by default).</param>
    public static GenericScreen Build(
        IReadOnlyList<GenericNodeSpec> nodes,
        IReadOnlyList<GenericSplitSpec>? splits = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            ArgumentNullException.ThrowIfNull(node);
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Id);
            if (!seen.Add(node.Id))
            {
                throw new ArgumentException($"Duplicate node id '{node.Id}'.", nameof(nodes));
            }

            if (float.IsNaN(node.MinWidth) || float.IsNaN(node.MinHeight))
            {
                throw new ArgumentException($"Node '{node.Id}' has an invalid minimum.", nameof(nodes));
            }
        }

        var byId = new Dictionary<string, GenericNodeSpec>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
        {
            byId[nodes[i].Id] = nodes[i];
        }

        var tree = new LayoutTree();
        var screen = new GenericScreen(tree);
        var root = new GenericContentPanel(nodes[0]);
        tree.AddRoot(root);
        screen._panels[root.Id] = root;
        screen._focusOrder.Add(root.Id);
        screen.FocusedId = root.Id;
        root.Focused = true;

        if (splits is not null)
        {
            for (int i = 0; i < splits.Count; i++)
            {
                var split = splits[i];
                ArgumentNullException.ThrowIfNull(split);
                if (!byId.TryGetValue(split.NewPanelId, out var spec))
                {
                    throw new KeyNotFoundException($"split seats unknown node '{split.NewPanelId}'");
                }

                if (screen._panels.ContainsKey(spec.Id))
                {
                    throw new ArgumentException($"split seats already-seated node '{spec.Id}'", nameof(splits));
                }

                float ratio = Math.Clamp(split.Ratio, 0.05f, 0.95f);
                var panel = new GenericContentPanel(spec);
                tree.Split(split.TargetId, split.Direction, ratio, panel, split.Gap);
                screen._panels[panel.Id] = panel;
                screen._focusOrder.Add(panel.Id);
            }
        }
        else
        {
            for (int i = 1; i < nodes.Count; i++)
            {
                var spec = nodes[i];
                var panel = new GenericContentPanel(spec);
                tree.Split(screen._focusOrder[0], SplitDir.Vertical, 1f - (i / (float)(nodes.Count + 1)), panel, gap: 0);
                screen._panels[panel.Id] = panel;
                screen._focusOrder.Add(panel.Id);
            }
        }

        return screen;
    }

    /// <summary>Resolves every panel rect for the given viewport.</summary>
    public void Solve(int width, int height) => Tree.Solve(width, height);

    /// <summary>Paints every panel (plus overlays) into <paramref name="buffer"/>.</summary>
    public void PaintAll(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Tree.PaintAll(buffer);
    }

    /// <summary>Panel by id (null when unknown).</summary>
    public GenericContentPanel? TryGetPanel(string id) =>
        _panels.TryGetValue(id, out var panel) ? panel : null;

    /// <summary>Focused panel (null when empty).</summary>
    public GenericContentPanel? FocusedPanel() =>
        FocusedId is not null && _panels.TryGetValue(FocusedId, out var panel) ? panel : null;

    /// <summary>Moves focus by a signed delta (wraps around).</summary>
    public void MoveFocus(int delta)
    {
        if (_focusOrder.Count == 0)
        {
            return;
        }

        int idx = FocusedId is not null ? _focusOrder.IndexOf(FocusedId) : 0;
        if (idx < 0)
        {
            idx = 0;
        }

        int next = ((_focusOrder.Count + idx + delta) % _focusOrder.Count + _focusOrder.Count) % _focusOrder.Count;
        SetFocus(_focusOrder[next]);
    }

    /// <summary>Focuses a panel by id (no-op when unknown).</summary>
    public void SetFocus(string id)
    {
        if (!_panels.TryGetValue(id, out var panel))
        {
            return;
        }

        if (FocusedId is not null && _panels.TryGetValue(FocusedId, out var prev))
        {
            prev.Focused = false;
        }

        FocusedId = id;
        panel.Focused = true;
    }

    /// <summary>
    /// Routes frame keys the screen owns (focus motion); everything else goes
    /// to the focused field. Judges no runes here — the hosted fields own
    /// their typed-input gates — so this site declares no key family.
    /// </summary>
    /// <returns>True when consumed.</returns>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release || _panels.Count == 0)
        {
            return false;
        }

        bool shift = (key.Modifiers & KeyModifiers.Shift) != 0;
        switch (key.Key)
        {
            case KeyCode.Tab:
                MoveFocus(shift ? -1 : 1);
                return true;
            case KeyCode.Escape:
                return true;
            case KeyCode.Up when shift:
                MoveFocus(-1);
                return true;
            case KeyCode.Down when shift:
                MoveFocus(1);
                return true;
            default:
                return FocusedPanel()?.Field?.HandleKey(in key) ?? false;
        }
    }
}
