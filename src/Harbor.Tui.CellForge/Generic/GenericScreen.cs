using Harbor.Tui.CellForge.Generic.Widgets;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
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
        Ensure.NotNull(spec);
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

    /// <summary>
    /// Content area below the title row (ratatui <c>Block::inner</c>): the
    /// first row is container chrome when <see cref="Title"/> is set. The
    /// hosted field / static lines start at this origin, never at
    /// <c>Rect.Y + 1</c> recomputed by hand (<see cref="Paint"/> seeds its
    /// cursor from here).
    /// </summary>
    public override Rect ContentRect => Title.Length > 0
        ? new Rect(Rect.X, Rect.Y + 1, Rect.Width, Math.Max(0, Rect.Height - 1))
        : Rect;

    public override void Paint(ScreenBuffer buffer)
    {
        Ensure.NotNull(buffer);
        if (Rect.Width <= 0 || Rect.Height <= 0)
        {
            return;
        }

        buffer.Fill(Rect, Cell.Blank);

        // Title row is container chrome; children paint from ContentRect.
        var content = ContentRect;
        int y = Rect.Y;
        int bottom = content.Bottom;
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
                Field.Paint(buffer, new Rect(content.X, y, content.Width, h));
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

            buffer.SetText(content.X, y, line.AsSpan(0, Math.Min(line.Length, content.Width)), BodyStyle);
        }
    }
}

/// <summary>
/// Local screen mode (epic #1195, part of #1207): every non-chat screen owns
/// its mode enum with an explicit terminal value instead of smearing modes
/// across <c>UiState</c>, the reducer and routers. Sources: input-form
/// <c>AppState{Running,Cancelled,Submitted}</c> (the form result is the exit
/// value) and demo2 <c>Mode{Running,Destroy,Quit}</c> (the overlay is a mode,
/// not flags). New screens copy this shape with their own terminal values and
/// never touch the global <c>UiState</c> for screen lifetime.
/// </summary>
public enum GenericScreenMode
{
    /// <summary>Screen is live; keys route to focus motion and hosted fields.</summary>
    Running,

    /// <summary>Terminal: accepted or completed — the screen's exit value is ready.</summary>
    Done,

    /// <summary>Terminal: dismissed without a result (Escape).</summary>
    Cancelled,
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
    private readonly GenericLayoutPlan _plan;

    private GenericScreen(LayoutTree tree, GenericLayoutPlan plan)
    {
        Tree = tree;
        _plan = plan;
    }

    /// <summary>Assembled layout tree (solved per frame via <see cref="Solve"/>).</summary>
    public LayoutTree Tree { get; }

    /// <summary>Normalized spec this screen was assembled from.</summary>
    public GenericLayoutPlan Plan => _plan;

    /// <summary>Panel count.</summary>
    public int Count => _panels.Count;

    /// <summary>Focus order (node order).</summary>
    public IReadOnlyList<string> FocusOrder => _focusOrder;

    /// <summary>Focused panel id (null when empty).</summary>
    public string? FocusedId { get; private set; }

    /// <summary>Local screen mode (starts <see cref="GenericScreenMode.Running"/>).</summary>
    public GenericScreenMode Mode { get; private set; } = GenericScreenMode.Running;

    /// <summary>True once <see cref="Mode"/> reaches a terminal value.</summary>
    public bool IsTerminal => Mode is GenericScreenMode.Done or GenericScreenMode.Cancelled;

    /// <summary>
    /// Moves <see cref="Mode"/> to a terminal value (first result wins: a
    /// terminal screen stays terminal).
    /// </summary>
    /// <param name="terminal">Only terminal values (<c>Done</c>, <c>Cancelled</c>).</param>
    public void Complete(GenericScreenMode terminal)
    {
        if (terminal != GenericScreenMode.Done && terminal != GenericScreenMode.Cancelled)
        {
            throw new ArgumentOutOfRangeException(nameof(terminal), terminal, "Only terminal values (Done, Cancelled) complete a screen.");
        }

        if (!IsTerminal)
        {
            Mode = terminal;
        }
    }

    /// <summary>
    /// Builds the screen from nodes plus splits. The spec is normalized to a
    /// fixpoint first (<see cref="GenericLayoutPlan.Normalize"/>), so every
    /// node but the root is seated exactly once. Pure setup-time work (no
    /// buffer writes, no store access) so tests can assert the tree shape
    /// without a terminal.
    /// </summary>
    /// <param name="nodes">Leaves in seating order (at least one).</param>
    /// <param name="splits">Splits seating nodes 1..N (one per extra node by default).</param>
    public static GenericScreen Build(
        IReadOnlyList<GenericNodeSpec> nodes,
        IReadOnlyList<GenericSplitSpec>? splits = null) =>
        Reconcile(null, nodes, splits);

    /// <summary>
    /// Rebuilds the screen from a new layout spec, reconciling by stable node
    /// id: a panel that keeps its id is reused as-is, so its scroll offset
    /// (inside the hosted field), geometry flags and focus survive the layout
    /// change. Only new ids get fresh panels; ids that disappeared are
    /// dropped. Focus falls back to the root when the focused id was removed,
    /// and <see cref="Mode"/> carries over. Content edits to a surviving panel
    /// go through <see cref="LayoutTree.Update"/> rather than this method.
    /// </summary>
    public GenericScreen Rebuild(
        IReadOnlyList<GenericNodeSpec> nodes,
        IReadOnlyList<GenericSplitSpec>? splits = null) =>
        Reconcile(this, nodes, splits);

    private static GenericScreen Reconcile(
        GenericScreen? existing,
        IReadOnlyList<GenericNodeSpec> nodes,
        IReadOnlyList<GenericSplitSpec>? splits)
    {
        var plan = GenericLayoutPlan.Normalize(nodes, splits);
        var byId = new Dictionary<string, GenericNodeSpec>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byId[node.Id] = node;
        }

        // Assemble from panel objects, not factories: reusing the object by
        // id is exactly what carries per-panel state across a layout change.
        GenericContentPanel Seat(string id) =>
            existing is not null && existing._panels.TryGetValue(id, out var previous)
                ? previous
                : new GenericContentPanel(byId[id]);

        var tree = new LayoutTree();
        var screen = new GenericScreen(tree, plan);

        var root = Seat(plan.RootId);
        tree.AddRoot(root);
        screen.Register(root);

        foreach (var split in plan.Splits)
        {
            var panel = Seat(split.NewPanelId);
            tree.Split(split.TargetId, split.Direction, split.Ratio, panel, split.Gap);
            screen.Register(panel);
        }

        string focusedId = existing?.FocusedId is string kept && screen._panels.ContainsKey(kept)
            ? kept
            : plan.RootId;
        foreach (var panel in screen._panels.Values)
        {
            panel.Focused = false;
        }

        screen._panels[focusedId].Focused = true;
        screen.FocusedId = focusedId;

        if (existing is not null)
        {
            screen.Mode = existing.Mode;
        }

        return screen;
    }

    private void Register(GenericContentPanel panel)
    {
        _panels[panel.Id] = panel;
        _focusOrder.Add(panel.Id);
    }

    /// <summary>Resolves every panel rect for the given viewport.</summary>
    public void Solve(int width, int height) => Tree.Solve(width, height);

    /// <summary>Paints every panel (plus overlays) into <paramref name="buffer"/>.</summary>
    public void PaintAll(ScreenBuffer buffer)
    {
        Ensure.NotNull(buffer);
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
                Complete(GenericScreenMode.Cancelled);
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
