using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     One tab as the strip will actually paint it: an index into
///     <see cref="TabStripState.Tabs" /> plus the cells it was given.
/// </summary>
/// <param name="Index">Position in tab order — the strip's own identity for the tab.</param>
/// <param name="Title">Sanitised title, already truncated to <paramref name="TitleCells" />.</param>
/// <param name="TitleCells">Cells granted to <paramref name="Title" /> (always >= 1 when rendered).</param>
/// <param name="X">Left cell, absolute (viewport) column.</param>
/// <param name="Width">Total cells reserved: dot + gap + title + markers + padding.</param>
/// <param name="IsActive">Whether this is the focused tab (drives the underline and the style).</param>
/// <param name="HasDirtyMarker">Draw the "uncommitted work" marker after the title.</param>
/// <param name="HasErrorMarker">The unread signal is a failure: the marker paints error, not amber (#1173).</param>
/// <param name="HasPinMarker">Draw the pinned marker before the status dot.</param>
/// <param name="Status">Short status text driving the dot colour (never painted verbatim).</param>
public readonly record struct TabCell(
    int Index,
    string Title,
    int TitleCells,
    int X,
    int Width,
    bool IsActive,
    bool HasDirtyMarker,
    bool HasErrorMarker,
    bool HasPinMarker,
    string Status)
{
    /// <summary>Left cell of the title text (after pin marker, dot and gap).</summary>
    public int TitleX =>
        X + SessionTabStripPanel.PinWidth + SessionTabStripPanel.DotWidth + SessionTabStripPanel.GapWidth;
}

/// <summary>
///     Immutable result of laying the strip out for one viewport width: which
///     tabs are visible, where each one starts, and how many are off-screen.
///     Pure — no buffer access, no state — so the geometry is testable on its own
///     and the painter stays a dumb writer of this plan.
/// </summary>
/// <param name="IsVisible">False when the strip must not paint at all (see <see cref="TabStripState.ShouldRender" />).</param>
/// <param name="FirstVisible">Index of the leftmost visible tab.</param>
/// <param name="Cells">Visible tabs, left to right, each with its absolute x.</param>
/// <param name="HiddenLeft">Tabs scrolled off the left edge.</param>
/// <param name="HiddenRight">Tabs scrolled off the right edge.</param>
/// <param name="OverflowX">Absolute x of the trailing <c>+N</c> indicator, or -1 when there is none.</param>
/// <param name="MoreLeft">Whether tabs are hidden left of the window (drives the <c>&lt;</c> hint).</param>
public readonly record struct TabStripPlan(
    bool IsVisible,
    int FirstVisible,
    ImmutableArray<TabCell> Cells,
    int HiddenLeft,
    int HiddenRight,
    int OverflowX,
    bool MoreLeft)
{
    /// <summary>Empty plan: nothing to paint.</summary>
    public static readonly TabStripPlan Hidden = new(false, 0, ImmutableArray<TabCell>.Empty, 0, 0, -1, false);

    /// <summary>How many tabs the window could not show, from either side.</summary>
    public int HiddenTotal => HiddenLeft + HiddenRight;
}

/// <summary>
///     State-driven session tab strip (#389, slice 2/3) — the render half of the
///     tab model that #388 put in <see cref="TabStripState" />.
/// </summary>
/// <remarks>
/// <para>
///     Unlike the host-agnostic <c>Widgets.Tabs</c> block, this panel owns
///     <b>no selection state</b>: the focused tab is
///     <see cref="TabStripState.ActiveTabId" />, read fresh every frame, and
///     every interaction is a <see cref="AppMsg" /> handed to the injected
///     <see cref="Dispatch" /> sink (the reducer decides, the store commits). The
///     panel raises no events and never mutates the state it was handed — a
///     mouse click and a keypress converge on the same transition.
/// </para>
/// <para>
///     <b>Geometry.</b> The strip is a minimum-fit window that always contains the
///     focused tab, with leftover columns handed back as title width. Titles are
///     sanitised (control characters dropped) and truncated to the cells the plan
///     granted them, so a pathological title can never produce a negative span or
///     run under the composer. Trailing tabs are summarised as <c>+N</c>; leading
///     ones as <c>&lt;</c>.
/// </para>
/// <para>
///     <b>Rows.</b> Two rows are requested (titles + an underline under the
///     focused tab). The layout solver may hand over fewer — and does hand over
///     none while the strip is hidden — so <see cref="Paint" /> clamps to its rect
///     and degrades to a single reverse-styled titles row rather than writing
///     outside it.
/// </para>
/// <para>
///     <b>Unread.</b> A tab with an unread signal (#1173) draws a static
///     marker after its title: amber for activity, error-red for a failed run
///     (opencode's <c>"activity" | "error"</c> split). When the signal clears,
///     the marker does not blink out — it settles through
///     <see cref="GlowEffect.TabMarkerFade" /> over a few painted frames. The
///     sweep animation itself is intentionally not ported (no Renderable clock
///     on a cell-diff surface); the static marker plus the settle drain is the
///     whole motion vocabulary here.
/// </para>
/// </remarks>
public sealed class SessionTabStripPanel : Panel
{
    /// <summary>Layout-tree id of the tab-strip leaf.</summary>
    public const string DefaultId = "chat.tabs";

    /// <summary>Rows the strip asks the layout for: titles + focused-tab underline.</summary>
    public const int PreferredRows = 2;

    /// <summary>Cells for a tab's status dot (colour carries the status; the glyph does not).</summary>
    public const int DotWidth = 1;

    /// <summary>Cells between the leading markers and the title.</summary>
    public const int GapWidth = 1;

    /// <summary>Cells of the pinned marker (only when <see cref="SessionTab.IsPinned" />).</summary>
    public const int PinWidth = 1;

    /// <summary>Cells of the dirty marker (only when <see cref="SessionTab.IsDirty" />).</summary>
    public const int DirtyWidth = 1;

    /// <summary>Padding cells around a tab's text (one left, one right).</summary>
    public const int PadWidth = 2;

    /// <summary>Cells between two adjacent tabs.</summary>
    public const int SeparatorWidth = 1;

    /// <summary>Shortest title the strip will ever draw.</summary>
    public const int MinTitleCells = 1;

    /// <summary>Longest title the strip will draw before the window runs out.</summary>
    public const int MaxTitleCells = 32;

    /// <summary>Status dot — width 1 in <c>UnicodeWidth</c>, so cells never drift.</summary>
    private const char StatusGlyph = '●';

    /// <summary>Pinned marker.</summary>
    private const char PinGlyph = '▪';

    /// <summary>Dirty marker (uncommitted work in the session).</summary>
    private const char DirtyGlyph = '•';

    /// <summary>"tabs hidden on the left" hint.</summary>
    private const char MoreLeftGlyph = '<';

    /// <summary>Focused-tab underline.</summary>
    private const char UnderlineGlyph = '▁';

    private static readonly CellStyle ActiveStyle = new(attrs: StyleAttr.Reverse);
    private static readonly CellStyle InactiveTitleStyle = ChatPalette.Dim;
    private static readonly CellStyle PinnedStyle = new(ChatPalette.Accent);
    private static readonly CellStyle DirtyStyle = new(ChatPalette.Warning);
    private static readonly CellStyle ErrorMarkerStyle = new(ChatPalette.Error);
    private static readonly CellStyle OverflowStyle = ChatPalette.Dim;

    /// <summary>
    ///     Painted frames a just-cleared unread marker lingers for (#1173).
    ///     The fade is frame-counted, not clocked: each <see cref="Paint" />
    ///     drains one tick through <see cref="GlowEffect.TabMarkerFade" />.
    /// </summary>
    internal const int MarkerFadeTicks = 4;

    /// <summary>Ticks left per settling tab, by session-id string. Cosmetic only — never state.</summary>
    private readonly Dictionary<string, int> _settle = new(StringComparer.Ordinal);

    /// <summary>Session ids that carried an unread signal on the last painted frame.</summary>
    private HashSet<string> _lastUnread = new(StringComparer.Ordinal);

    /// <summary>Latest tab-strip snapshot, projected by the renderer (never mutated here).</summary>
    public TabStripState Strip { get; set; } = TabStripState.Empty;

    /// <summary>
    ///     Where mutations go — normally <c>store.Dispatch</c>. Null (tests, hosts
    ///     that never open tabs) makes the panel read-only, which is the safe
    ///     default: a display widget must never be the only way to reach state.
    /// </summary>
    public Action<AppMsg>? Dispatch { get; set; }

    /// <summary>Plan produced by the last <see cref="Layout" /> call (test seam).</summary>
    public TabStripPlan LastPlan { get; private set; } = TabStripPlan.Hidden;

    public SessionTabStripPanel(string id = DefaultId, int priority = 20)
        // Min 0x0: the strip is chrome, so it must be the first thing the solver
        // sacrifices. With PreferredRows unclaimed the rect collapses to nothing
        // and Paint is a no-op, which is what keeps every pre-#389 golden
        // byte-identical.
        : base(id, new Size(0, 0), priority)
    {
    }

    /// <summary>
    ///     Lays the strip out for <paramref name="width" /> columns. Pure: same
    ///     inputs, same plan, no buffer and no mutation — the reason the geometry
    ///     can be asserted directly by tests instead of through a screenshot.
    /// </summary>
    public TabStripPlan Layout(int width)
    {
        var strip = Strip;
        var tabs = strip.Tabs;
        if (!strip.ShouldRender || tabs.Length == 0 || width <= 0)
            return TabStripPlan.Hidden;

        int active = strip.ActiveTabId is { } id ? strip.IndexOf(id) : 0;
        if (active < 0)
            active = 0;

        // Narrowest each tab can be drawn at: status dot, gap, a one-cell title,
        // the two optional markers and the padding around them.
        int[] minima = new int[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
            minima[i] = DotWidth + GapWidth + PadWidth + MinTitleCells
                        + (tabs[i].IsPinned ? PinWidth : 0)
                        + (tabs[i].HasUnread ? DirtyWidth : 0);

        // Grow a window outwards from the focused tab so it is always inside it,
        // right first then left, alternating: a strip that always shows the tab
        // you are on beats one that always starts at tab 0.
        int lo = active, hi = active;
        int spent = minima[active];
        bool growRight = true;
        while (true)
        {
            // Both sides are tried every step and the preference only breaks
            // ties. Forcing strict alternation would stop the moment one side
            // filled up, which is exactly the case that still has room on the
            // other — the strip would then waste the row it was given.
            bool rightFits = hi + 1 < tabs.Length
                             && spent + SeparatorWidth + minima[hi + 1] <= width;
            bool leftFits = lo > 0
                            && spent + SeparatorWidth + minima[lo - 1] <= width;

            int step;
            if (rightFits && (leftFits == growRight || !leftFits))
                step = 1;
            else if (leftFits)
                step = -1;
            else
                break;

            spent += SeparatorWidth + minima[step > 0 ? hi + 1 : lo - 1];
            if (step > 0)
                hi++;
            else
                lo--;

            growRight = !growRight;
        }

        // The "N more" summary and the "<" hint were not in the growth budget, so
        // shrink until they fit. The focused tab is never dropped, and the side
        // that is farther from it goes first so the scroll tracks the focus.
        int hiddenLeft = lo;
        int hiddenRight = tabs.Length - 1 - hi;
        while (lo < active || hi > active)
        {
            int reserve = MoreLeftCells(hiddenLeft) + OverflowCells(hiddenRight);
            if (spent + reserve <= width)
                break;

            if (hi > active && (lo == active || active - lo <= hi - active))
            {
                spent -= SeparatorWidth + minima[hi];
                hi--;
                hiddenRight++;
            }
            else
            {
                spent -= SeparatorWidth + minima[lo];
                lo++;
                hiddenLeft++;
            }
        }

        int moreIndicator = MoreLeftCells(hiddenLeft);
        int overflowCells = OverflowCells(hiddenRight);
        int available = width - moreIndicator - overflowCells;

        // Hand the slack back as title width, one cell at a time. The focused tab
        // is served first (a focused tab you cannot read is the one failure that
        // matters), then the rest round-robin so no single tab hoards the space.
        int count = hi - lo + 1;
        int[] granted = new int[count];
        for (int i = 0; i < count; i++)
            granted[i] = MinTitleCells;

        int activeSlot = active - lo;
        int slack = available - spent;
        while (slack > 0)
        {
            bool grew = false;

            // The focused tab is served first: an active tab the user cannot read
            // is the one failure that matters.
            if (granted[activeSlot] < MaxTitleCells)
            {
                granted[activeSlot]++;
                slack--;
                grew = true;
            }

            // Then round-robin the rest in tab order, so no single tab hoards
            // the leftover space and the row stays visually even.
            for (int slot = 0; slot < count && slack > 0; slot++)
            {
                if (slot == activeSlot || granted[slot] >= MaxTitleCells)
                    continue;
                granted[slot]++;
                slack--;
                grew = true;
            }

            if (!grew)
                break;
        }

        var cells = ImmutableArray.CreateBuilder<TabCell>(count);
        int x = moreIndicator;
        for (int i = 0; i < count; i++)
        {
            var tab = tabs[lo + i];
            int titleCells = granted[i];
            int cellWidth = minima[lo + i] - MinTitleCells + titleCells;
            cells.Add(new TabCell(
                Index: lo + i,
                Title: Truncate(Clean(tab.Title), titleCells),
                TitleCells: titleCells,
                X: x,
                Width: cellWidth,
                IsActive: lo + i == active,
                HasDirtyMarker: tab.HasUnread,
                HasErrorMarker: tab.HasError,
                HasPinMarker: tab.IsPinned,
                Status: tab.ShortStatus));
            x += cellWidth + SeparatorWidth;
        }

        // The focus is guaranteed a window but not necessarily a whole one, so
        // the summary is positioned by hand rather than trusted to the cell
        // walk: it starts in the separator slot after the last visible tab, and
        // is pulled back to whatever still fits. Without the pull-back a strip
        // narrower than its own "+N" would report an x past the row.
        int overflowX = -1;
        if (overflowCells > 0)
        {
            int limit = Math.Max(0, width - overflowCells);
            overflowX = Math.Min(Math.Max(moreIndicator, x - SeparatorWidth), limit);
        }

        return new TabStripPlan(
            IsVisible: true,
            FirstVisible: lo,
            Cells: ClampToWidth(cells.ToImmutable(), width),
            HiddenLeft: hiddenLeft,
            HiddenRight: hiddenRight,
            OverflowX: overflowX,
            MoreLeft: hiddenLeft > 0);
    }

    /// <summary>
    ///     Clamps a laid-out row to <paramref name="width" /> columns. A tab's
    ///     markers and dot are never dropped — only the title shrinks — so the
    ///     leftmost cells of a squeezed strip stay meaningful.
    /// </summary>
    private static ImmutableArray<TabCell> ClampToWidth(ImmutableArray<TabCell> cells, int width)
    {
        if (cells.Length == 0)
            return cells;

        var clamped = cells;
        for (int i = 0; i < clamped.Length; i++)
        {
            var cell = clamped[i];
            // A cell that starts past the row can never be seen, so pin it to the
            // right edge with zero width rather than leaving a negative span.
            int x = Math.Min(cell.X, width);
            int room = Math.Max(0, width - x);
            if (x == cell.X && room >= cell.Width)
                continue;

            int markers = DotWidth + GapWidth
                          + (cell.HasPinMarker ? PinWidth : 0)
                          + (cell.HasDirtyMarker ? DirtyWidth : 0)
                          + PadWidth;
            int titleCells = Math.Clamp(room - markers, 0, cell.TitleCells);
            clamped = clamped.SetItem(
                i,
                cell with
                {
                    X = x,
                    TitleCells = titleCells,
                    Title = Truncate(cell.Title, titleCells),
                    Width = room
                });
        }

        return clamped;
    }

    /// <summary>
    ///     Paints the plan into <paramref name="buffer" />: titles on the first
    ///     row, the focused tab's underline on the second when the rect has one.
    ///     Bounds-safe by construction — every write is clamped to
    ///     <see cref="Panel.Rect" />, so a squeezed rect degrades instead of
    ///     bleeding into the composer.
    /// </summary>
    public override void Paint(ScreenBuffer buffer)
    {
        var rect = Rect;
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var plan = Layout(rect.Width);
        LastPlan = plan;
        if (!plan.IsVisible)
            return;

        // The back buffer persists across frames, so a strip that shrinks must
        // erase the row it no longer uses before anything is drawn.
        buffer.Fill(rect, Cell.Blank);

        TrackSettle();

        for (int i = 0; i < plan.Cells.Length; i++)
        {
            var cell = plan.Cells[i];
            double settle = cell.HasDirtyMarker ? 1.0 : SettleLevel(cell.Index);
            PaintCell(buffer, rect, cell, settle);
        }

        if (plan.MoreLeft)
            buffer.SetText(rect.X, rect.Y, MoreLeftGlyph.ToString(), OverflowStyle);

        if (plan.OverflowX >= 0)
        {
            buffer.SetText(
                rect.X + plan.OverflowX,
                rect.Y,
                OverflowLabel(plan.HiddenRight),
                OverflowStyle);
        }

        if (rect.Height >= PreferredRows)
            PaintUnderline(buffer, rect, plan);
    }

    // ── interactions: dispatch only, never mutate ───────────────────────────

    /// <summary>
    ///     Tab index at absolute column <paramref name="x" />, or -1. Reads the
    ///     last plan, so it agrees with what is on screen.
    /// </summary>
    public int HitTest(int x)
    {
        var cells = LastPlan.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            if (x >= cell.X && x < cell.X + cell.Width)
                return cell.Index;
        }

        return -1;
    }

    /// <summary>Dispatch <c>ActivateTab</c> for the tab under <paramref name="x" />. False when nothing was hit.</summary>
    public bool ActivateAt(int x) => Send(ActiveTabOf(x));

    /// <summary>Dispatch <c>CloseTab</c> for the tab under <paramref name="x" />. False when nothing was hit.</summary>
    public bool CloseAt(int x) => Send(CloseTabOf(x));

    /// <summary>Dispatch <c>CycleNextTab</c>.</summary>
    public bool Next() => Send(new ChatAppMsg.CycleNextTab());

    /// <summary>Dispatch <c>CyclePreviousTab</c>.</summary>
    public bool Previous() => Send(new ChatAppMsg.CyclePreviousTab());

    /// <summary>Dispatch <c>ReopenTab</c> — restore the most recently closed tab (#1173).</summary>
    public bool Reopen() => Send(new ChatAppMsg.ReopenTab());

    /// <summary>Dispatch <c>CycleNextUnreadTab</c>.</summary>
    public bool NextUnread() => Send(new ChatAppMsg.CycleNextUnreadTab());

    /// <summary>Dispatch <c>CyclePreviousUnreadTab</c>.</summary>
    public bool PreviousUnread() => Send(new ChatAppMsg.CyclePreviousUnreadTab());

    /// <summary>
    ///     Dispatch <c>ActivateTabSlot</c> for one-based <paramref name="slot" />
    ///     (#1173 — <c>Ctrl+1</c>..<c>Ctrl+9</c>). False without dispatching
    ///     when the slot is outside the strip on screen.
    /// </summary>
    public bool ActivateSlot(int slot) =>
        slot >= 1 && slot <= 9 && slot <= Strip.Tabs.Length && Send(new ChatAppMsg.ActivateTabSlot(slot));

    /// <summary>
    ///     Hands <paramref name="msg" /> to the sink, if one is wired. Named
    ///     <c>Send</c> rather than <c>Dispatch</c> so it cannot be confused with
    ///     the <see cref="Dispatch" /> property it reads.
    /// </summary>
    private bool Send(AppMsg? msg)
    {
        if (msg is null || Dispatch is not { } sink)
            return false;
        sink(msg);
        return true;
    }

    private AppMsg? ActiveTabOf(int x) =>
        HitTest(x) is var index && index >= 0 && index < Strip.Tabs.Length
            ? new ChatAppMsg.ActivateTab(Strip.Tabs[index].SessionId)
            : null;

    private AppMsg? CloseTabOf(int x) =>
        HitTest(x) is var index && index >= 0 && index < Strip.Tabs.Length
            ? new ChatAppMsg.CloseTab(Strip.Tabs[index].SessionId)
            : null;

    // ── painting helpers ────────────────────────────────────────────────────

    /// <summary>
    ///     Settle level for the tab at <paramref name="tabIndex" />: full (1)
    ///     while unread, a draining <see cref="GlowEffect.TabMarkerFade" />
    ///     level for a few frames after the signal clears, 0 otherwise. Reads
    ///     the cosmetic <see cref="_settle" /> map only — never state.
    /// </summary>
    private double SettleLevel(int tabIndex)
    {
        var tabs = Strip.Tabs;
        if ((uint)tabIndex >= (uint)tabs.Length)
            return 0.0;
        return _settle.TryGetValue(tabs[tabIndex].SessionId.Value, out int left)
            ? GlowEffect.TabMarkerFade(left, MarkerFadeTicks)
            : 0.0;
    }

    /// <summary>
    ///     Advances the marker settle (#1173): tabs that lost their unread
    ///     signal since the last painted frame start draining, re-marked tabs
    ///     cancel their drain, finished entries leave the map. One tick per
    ///     painted frame — the frame-counted stand-in for opencode's clocked
    ///     fade. Purely cosmetic: nothing here touches the store.
    /// </summary>
    private void TrackSettle()
    {
        var tabs = Strip.Tabs;
        var unread = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].HasUnread)
                unread.Add(tabs[i].SessionId.Value);
        }

        // A re-marked tab shows the full marker again — its drain is over.
        foreach (var id in unread)
            _settle.Remove(id);

        if (_settle.Count > 0)
        {
            var ids = new List<string>(_settle.Keys);
            for (int i = 0; i < ids.Count; i++)
            {
                int left = _settle[ids[i]] - 1;
                if (left <= 0)
                    _settle.Remove(ids[i]);
                else
                    _settle[ids[i]] = left;
            }
        }

        foreach (var id in _lastUnread)
        {
            if (!unread.Contains(id) && !_settle.ContainsKey(id))
                _settle[id] = MarkerFadeTicks;
        }

        _lastUnread = unread;
    }

    private void PaintCell(ScreenBuffer buffer, in Rect rect, in TabCell cell, double settleLevel)
    {
        int y = rect.Y;

        if (cell.HasPinMarker)
            buffer.SetText(rect.X + cell.X, y, PinGlyph.ToString(), PinnedStyle);

        buffer.SetText(
            rect.X + cell.X + (cell.HasPinMarker ? PinWidth : 0),
            y,
            StatusGlyph.ToString(),
            StatusStyle(cell.Status));

        buffer.SetText(
            rect.X + cell.TitleX,
            y,
            cell.Title.AsSpan(),
            cell.IsActive ? ActiveStyle : InactiveTitleStyle);

        // The marker: full colour while unread (error-red wins over amber),
        // a dimming linger for a few frames after the signal clears. A
        // settling tab reserved no marker cell, so it draws over its own
        // right-pad cell — one cell, always inside the tab's span.
        bool showMarker = cell.HasDirtyMarker || settleLevel > 0;
        if (showMarker)
        {
            var style = cell.HasDirtyMarker
                ? (cell.HasErrorMarker ? ErrorMarkerStyle : DirtyStyle)
                : (settleLevel >= 0.5 ? DirtyStyle : InactiveTitleStyle);
            buffer.SetText(
                rect.X + cell.TitleX + cell.TitleCells,
                y,
                DirtyGlyph.ToString(),
                style);
        }
    }

    private void PaintUnderline(ScreenBuffer buffer, in Rect rect, in TabStripPlan plan)
    {
        int y = rect.Y + 1;
        var cells = plan.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            if (!cells[i].IsActive)
                continue;

            var cell = cells[i];
            // Leading cell plus the rest of the tab: one continuous run reads as
            // an underline, a bare glyph would look like a stray cursor.
            buffer.SetText(rect.X + cell.X, y, new string(UnderlineGlyph, cell.Width), PinnedStyle);
            return;
        }
    }

    /// <summary>
    ///     Status dot colour: running → accent, error → error slot, anything
    ///     else → the muted tone. Unknown statuses degrade to muted rather than
    ///     picking an arbitrary palette entry, so a new
    ///     <see cref="SessionTab.ShortStatus" /> value can never repaint the
    ///     strip in a colour that means something else elsewhere.
    /// </summary>
    private static CellStyle StatusStyle(string? status)
    {
        if (string.Equals(status, "run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "running", StringComparison.Ordinal))
            return new CellStyle(ChatPalette.Accent);

        if (string.Equals(status, "err", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.Ordinal))
            return new CellStyle(ChatPalette.Error);

        return new CellStyle(ChatPalette.Muted);
    }

    // ── text helpers ────────────────────────────────────────────────────────

    /// <summary>Cells taken by the "+N" summary, or 0 when everything fits.</summary>
    private static int OverflowCells(int hidden) =>
        hidden <= 0 ? 0 : 1 + OverflowLabel(hidden).Length;

    /// <summary>The "+N" summary text (leading blank separates it from the last tab).</summary>
    private static string OverflowLabel(int hidden) =>
        hidden <= 0 ? string.Empty : "+" + hidden.ToString(CultureInfo.InvariantCulture);

    /// <summary>Cells taken by the "&lt;" hint, or 0 when nothing is hidden left.</summary>
    private static int MoreLeftCells(int hidden) => hidden > 0 ? 1 : 0;

    /// <summary>
    ///     Drops control characters and collapses newlines so a multi-line
    ///     session title cannot break the single-row strip. Also trims, because a
    ///     stray leading space would otherwise eat a cell of a narrow tab.
    /// </summary>
    internal static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "session";

        var sb = new StringBuilder(title.Length);
        bool lastWasSpace = true; // trims the left edge
        for (int i = 0; i < title.Length; i++)
        {
            char c = title[i];
            if (char.IsControl(c))
            {
                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            if (c == ' ')
            {
                if (lastWasSpace)
                    continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            sb.Append(c);
        }

        string cleaned = sb.ToString().TrimEnd();
        return cleaned.Length == 0 ? "session" : cleaned;
    }

    /// <summary>
    ///     Truncates to <paramref name="cells" /> display columns, never splitting
    ///     a wide glyph and never returning more cells than asked for — the
    ///     invariant that keeps the strip from painting over its neighbour.
    /// </summary>
    internal static string Truncate(string text, int cells)
    {
        if (cells <= 0)
            return string.Empty;
        if (UnicodeWidth.Width(text.AsSpan()) <= cells)
            return text;

        int width = 0;
        var sb = new StringBuilder(cells);
        var rest = text.AsSpan();
        while (!rest.IsEmpty
               && Rune.DecodeFromUtf16(rest, out Rune rune, out int consumed) == OperationStatus.Done)
        {
            int w = UnicodeWidth.Width(rune);
            if (width + w > cells)
                break;
            sb.Append(rune.ToString());
            width += w;
            rest = rest[consumed..];
        }

        return sb.ToString();
    }
}
