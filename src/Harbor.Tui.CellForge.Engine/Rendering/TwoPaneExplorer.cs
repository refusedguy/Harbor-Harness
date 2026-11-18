// #2698 P02: domain-free two-pane explorer state. Two prepared collections,
// one active pane, one caller-owned transfer. The primitive never touches the
// file system, a domain port or the screen layout: the caller supplies items
// each frame and decides what a transfer means. BCL-only, AOT-clean, render
// thread owned — the same leaf rule as SplitPane and BreadcrumbTrail.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>Identifies one of the two collections of a <see cref="TwoPaneState"/>.</summary>
public enum PaneId : byte
{
    /// <summary>The left / source collection.</summary>
    Source = 0,

    /// <summary>The right / destination collection.</summary>
    Target = 1,
}

/// <summary>
/// One prepared item. The primitive only knows its identity, its display text
/// and an optional preview line; a richer payload stays with the caller and is
/// looked up by <see cref="Id"/> when a transfer fires.
/// </summary>
/// <param name="Id">Stable identity inside the owning pane.</param>
/// <param name="Label">Single-line display text.</param>
/// <param name="Detail">Preview line for the selection. Empty means no preview.</param>
public readonly record struct PaneItem(string Id, string Label, string Detail = "");

/// <summary>
/// A fact reported to the caller: which pane the item came from, where it is
/// going, and which item was under the source cursor. Reporting a transfer
/// never mutates <see cref="TwoPaneState"/>; the caller rebuilds the two
/// collections and hands them back on the next frame.
/// </summary>
public readonly record struct TwoPaneTransfer(PaneId From, PaneId To, PaneItem Item);

/// <summary>One projected text row of a pane, with its cursor and focus flags.</summary>
public readonly record struct TwoPaneRow(string Text, bool IsCursor, bool IsActive);

/// <summary>
/// State for the two-pane explorer pattern (ranger, broot, lazydocker): two
/// collections side by side, one active at a time, with one transfer action.
/// The widget owns the two cursors and the active pane; the caller owns the
/// collections. Moving a cursor never touches the other pane, switching focus
/// never moves either cursor, and a transfer is a value returned to the host.
/// </summary>
/// <remarks>
/// <para>
/// The host binds keys through its existing key registry (Left/Right move the
/// active cursor, Tab switches panes, Enter transfers, the reverse modifier
/// transfers the other way). No key handling lives here, so no second binding
/// table is created.
/// </para>
/// <para>
/// <see cref="Rows"/> is a pure projection of the state for a clip rectangle,
/// and <see cref="Paint"/> places both panes in a <see cref="ScreenBuffer"/>
/// with a one-cell divider when there is room. The preview line follows the
/// selected item, so cursor movement and preview never disagree.
/// </para>
/// </remarks>
public sealed class TwoPaneState
{
    private IReadOnlyList<PaneItem> _source = Array.Empty<PaneItem>();
    private IReadOnlyList<PaneItem> _target = Array.Empty<PaneItem>();
    private int _sourceCursor;
    private int _targetCursor;
    private PaneId _active = PaneId.Source;

    /// <summary>Items supplied by the caller for the source pane.</summary>
    public IReadOnlyList<PaneItem> Source => _source;

    /// <summary>Items supplied by the caller for the target pane.</summary>
    public IReadOnlyList<PaneItem> Target => _target;

    /// <summary>Pane that receives cursor movement and a forward transfer.</summary>
    public PaneId Active => _active;

    /// <summary>Selected row index in <see cref="Source"/>.</summary>
    public int SourceCursor => _sourceCursor;

    /// <summary>Selected row index in <see cref="Target"/>.</summary>
    public int TargetCursor => _targetCursor;

    /// <summary>Items of one pane.</summary>
    public IReadOnlyList<PaneItem> Items(PaneId pane) => pane == PaneId.Source ? _source : _target;

    /// <summary>Selected row index of one pane.</summary>
    public int Cursor(PaneId pane) => pane == PaneId.Source ? _sourceCursor : _targetCursor;

    /// <summary>Item under one pane's cursor, if the pane is non-empty.</summary>
    public bool TryGetSelected(PaneId pane, out PaneItem item)
    {
        IReadOnlyList<PaneItem> items = Items(pane);
        int cursor = Cursor(pane);
        if (cursor >= 0 && cursor < items.Count)
        {
            item = items[cursor];
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Replaces one pane's collection, clamping that pane's cursor into range.
    /// The other pane is untouched. Returns true when the cursor was clamped.
    /// </summary>
    public bool Replace(PaneId pane, IReadOnlyList<PaneItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int limit = Math.Max(0, items.Count - 1);
        if (pane == PaneId.Source)
        {
            int next = Math.Clamp(_sourceCursor, 0, limit);
            bool clamped = next != _sourceCursor;
            _source = items;
            _sourceCursor = next;
            return clamped;
        }

        int targetNext = Math.Clamp(_targetCursor, 0, limit);
        bool targetClamped = targetNext != _targetCursor;
        _target = items;
        _targetCursor = targetNext;
        return targetClamped;
    }

    /// <summary>Sets one pane's cursor, clamped to its item count. Returns true on a move.</summary>
    public bool SetCursor(PaneId pane, int index)
    {
        int limit = Math.Max(0, Items(pane).Count - 1);
        int next = Math.Clamp(index, 0, limit);
        if (pane == PaneId.Source)
        {
            if (next == _sourceCursor)
            {
                return false;
            }

            _sourceCursor = next;
        }
        else
        {
            if (next == _targetCursor)
            {
                return false;
            }

            _targetCursor = next;
        }

        return true;
    }

    /// <summary>Moves the active pane's cursor by <paramref name="delta"/> rows.</summary>
    public bool MoveCursor(int delta) => MoveCursor(_active, delta);

    /// <summary>Moves one pane's cursor by a signed delta, clamped to its bounds.</summary>
    public bool MoveCursor(PaneId pane, int delta)
    {
        int limit = Math.Max(0, Items(pane).Count - 1);
        long next = (long)Cursor(pane) + delta;
        return SetCursor(pane, (int)Math.Clamp(next, 0, limit));
    }

    /// <summary>Changes which pane is active. Cursors are not moved.</summary>
    public bool SetActive(PaneId pane)
    {
        if (_active == pane)
        {
            return false;
        }

        _active = pane;
        return true;
    }

    /// <summary>Flips the active pane, the same operation Tab is bound to.</summary>
    public bool SwitchActive() => SetActive(Other(_active));

    /// <summary>
    /// Reports the transfer for the active pane, or for the other pane when
    /// <paramref name="reverse"/> is set. The item comes from the source side
    /// of the reported transfer. State is unchanged; the host applies the fact.
    /// </summary>
    public TwoPaneTransfer? Transfer(bool reverse = false)
    {
        PaneId from = reverse ? Other(_active) : _active;
        if (!TryGetSelected(from, out PaneItem item))
        {
            return null;
        }

        return new TwoPaneTransfer(from, Other(from), item);
    }

    /// <summary>
    /// Pure row projection for one pane: head with item count, a window of
    /// cursor-centred item rows, and the selected item's preview line. The same
    /// state and clip always produce the same rows.
    /// </summary>
    public IReadOnlyList<TwoPaneRow> Rows(PaneId pane, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<TwoPaneRow>();
        }

        IReadOnlyList<PaneItem> items = Items(pane);
        int cursor = Cursor(pane);
        bool active = pane == _active;
        var rows = new List<TwoPaneRow>(height)
        {
            new TwoPaneRow(
                UnicodeTruncation.Truncate($"{pane} ({items.Count})", width, string.Empty),
                false,
                active),
        };

        int bodyRows = Math.Max(0, height - 2);
        if (bodyRows > 0 && items.Count > 0)
        {
            int start = items.Count <= bodyRows
                ? 0
                : Math.Clamp(cursor - (bodyRows / 2), 0, items.Count - bodyRows);
            for (int i = start; i < items.Count && rows.Count < height - 1; i++)
            {
                string prefix = i == cursor ? "> " : "  ";
                rows.Add(new TwoPaneRow(
                    UnicodeTruncation.Truncate(prefix + items[i].Label, width, UnicodeTruncation.Ellipsis),
                    i == cursor,
                    active));
            }
        }

        if (height >= 2)
        {
            string detail = TryGetSelected(pane, out PaneItem selected) ? selected.Detail : string.Empty;
            rows.Add(new TwoPaneRow(
                UnicodeTruncation.Truncate(detail, width, UnicodeTruncation.Ellipsis),
                false,
                active));
        }

        return rows;
    }

    /// <summary>
    /// Paints both panes inside <paramref name="clip"/>. A one-cell divider is
    /// reserved when the clip is wide enough; the active pane keeps
    /// <paramref name="activeStyle"/>, the other uses
    /// <paramref name="inactiveStyle"/>, and the cursor row is reversed.
    /// </summary>
    public void Paint(
        ScreenBuffer buffer,
        Rect clip,
        CellStyle activeStyle = default,
        CellStyle inactiveStyle = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (clip.Width <= 0 || clip.Height <= 0)
        {
            return;
        }

        int gap = clip.Width >= 5 ? 1 : 0;
        int leftWidth = (clip.Width - gap) / 2;
        int rightWidth = clip.Width - gap - leftWidth;
        if (leftWidth <= 0 || rightWidth <= 0)
        {
            return;
        }

        PaintPane(
            buffer,
            new Rect(clip.X, clip.Y, leftWidth, clip.Height),
            PaneId.Source,
            activeStyle,
            inactiveStyle);
        PaintPane(
            buffer,
            new Rect(clip.X + leftWidth + gap, clip.Y, rightWidth, clip.Height),
            PaneId.Target,
            activeStyle,
            inactiveStyle);
    }

    private static PaneId Other(PaneId pane) => pane == PaneId.Source ? PaneId.Target : PaneId.Source;

    private void PaintPane(
        ScreenBuffer buffer,
        Rect rect,
        PaneId pane,
        CellStyle activeStyle,
        CellStyle inactiveStyle)
    {
        IReadOnlyList<TwoPaneRow> rows = Rows(pane, rect.Width, rect.Height);
        CellStyle paneStyle = pane == _active ? activeStyle : inactiveStyle;
        var cursorStyle = new CellStyle(attrs: StyleAttr.Reverse);

        for (int i = 0; i < rows.Count; i++)
        {
            TwoPaneRow row = rows[i];
            buffer.SetText(rect.X, rect.Y + i, row.Text.AsSpan(), row.IsCursor ? cursorStyle : paneStyle);
        }
    }
}
