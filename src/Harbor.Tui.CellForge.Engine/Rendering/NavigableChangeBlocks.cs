// #2706 P05: domain-free navigable change blocks with two-sided selection.
// The primitive owns two prepared sides, one active side, a block cursor, a
// line cursor and an optional selection anchor per side. It never knows what
// produced the blocks or what a transfer means: the caller supplies prepared
// blocks each frame and receives the selected range as a value. No git terms
// (hunk, stage) and no application type appear on the public surface. BCL-only,
// AOT-clean, render-thread owned — the same leaf rule as TwoPaneExplorer.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>One side of a change-block view.</summary>
public enum BlockSide : byte
{
    /// <summary>The left side.</summary>
    Left = 0,

    /// <summary>The right side.</summary>
    Right = 1,
}

/// <summary>One prepared line of a change block.</summary>
/// <param name="Number">Caller-supplied number drawn in the gutter.</param>
/// <param name="Text">Single-line content.</param>
public readonly record struct ChangeLine(int Number, string Text);

/// <summary>
/// One prepared change block: a stable identity, a display label and a
/// contiguous set of lines. The primitive does not know where the block came
/// from; a diff adapter, a log adapter or a list adapter can all supply it.
/// </summary>
/// <param name="Id">Stable identity supplied by the caller.</param>
/// <param name="Label">Short label drawn above the block's lines.</param>
/// <param name="Lines">Lines of the block, in display order.</param>
public sealed record ChangeBlock(string Id, string Label, IReadOnlyList<ChangeLine> Lines);

/// <summary>A half-open range of line indices <c>[Start, End)</c> inside one block.</summary>
/// <param name="Start">First selected line index, inclusive.</param>
/// <param name="End">One past the last selected line index, exclusive.</param>
public readonly record struct BlockLineRange(int Start, int End)
{
    /// <summary>Number of lines in the range, never negative.</summary>
    public int Length => Math.Max(0, End - Start);

    /// <summary>True when the range selects no line.</summary>
    public bool IsEmpty => End <= Start;

    /// <summary>True when <paramref name="index"/> lies inside the range.</summary>
    public bool Contains(int index) => index >= Start && index < End;
}

/// <summary>
/// A fact reported to the caller when a selection is handed to the other side.
/// Reporting a transfer never mutates <see cref="NavigableChangeBlocks"/>: the
/// caller applies the fact and rebuilds the two collections on the next frame.
/// </summary>
/// <param name="BlockId">Identity of the block that owns the selection.</param>
/// <param name="Range">Selected line range inside that block.</param>
/// <param name="From">Side the selection came from.</param>
/// <param name="To">Side the selection is going to.</param>
public readonly record struct BlockTransfer(string BlockId, BlockLineRange Range, BlockSide From, BlockSide To);

/// <summary>One projected row of one side of the change-block view.</summary>
/// <param name="Text">Truncated single-line text.</param>
/// <param name="IsCursor">True for the row under the line cursor.</param>
/// <param name="IsSelected">True for a line inside the current selection.</param>
/// <param name="IsActive">True when this side owns the focus.</param>
public readonly record struct ChangeRow(string Text, bool IsCursor, bool IsSelected, bool IsActive);

/// <summary>
/// State for the navigable change-blocks pattern (lazygit, gitui, tig): blocks
/// of lines on two sides, one active side, next/previous block navigation, and
/// a line selection that is either the cursor line or an anchored range. Both
/// sides keep their own block, line cursor and anchor, so moving or selecting
/// on one side never changes the other. A transfer is a value returned to the
/// host, not a mutation of a domain model.
/// </summary>
/// <remarks>
/// <para>
/// The host binds keys through its existing key registry (block navigation,
/// line movement, anchor toggle, switch side, transfer). No key handling lives
/// here, so no second binding table is created.
/// </para>
/// <para>
/// <see cref="Rows"/> is a pure projection of the state for a clip rectangle
/// and <see cref="Paint"/> places both sides in a <see cref="ScreenBuffer"/>
/// with a one-cell divider when there is room. The same state and clip always
/// produce the same rows.
/// </para>
/// </remarks>
public sealed class NavigableChangeBlocks
{
    private IReadOnlyList<ChangeBlock> _blocks = Array.Empty<ChangeBlock>();
    private int _leftBlock;
    private int _leftLine;
    private int _rightBlock;
    private int _rightLine;
    private int? _leftAnchor;
    private int? _rightAnchor;
    private BlockSide _active = BlockSide.Left;

    /// <summary>Blocks supplied by the caller for both sides.</summary>
    public IReadOnlyList<ChangeBlock> Blocks => _blocks;

    /// <summary>Side that receives navigation, selection and a forward transfer.</summary>
    public BlockSide Active => _active;

    /// <summary>Index of the focused block on one side.</summary>
    public int BlockIndex(BlockSide side) => side == BlockSide.Left ? _leftBlock : _rightBlock;

    /// <summary>Line cursor inside the focused block on one side.</summary>
    public int LineCursor(BlockSide side) => side == BlockSide.Left ? _leftLine : _rightLine;

    /// <summary>Anchor of a range selection on one side, or null for a single line.</summary>
    public int? Anchor(BlockSide side) => side == BlockSide.Left ? _leftAnchor : _rightAnchor;

    /// <summary>Index of the focused block on the active side.</summary>
    public int BlockIndex() => BlockIndex(_active);

    /// <summary>Line cursor on the active side.</summary>
    public int LineCursor() => LineCursor(_active);

    /// <summary>Block under one side's cursor, if the side has blocks.</summary>
    public bool TryGetBlock(BlockSide side, out ChangeBlock? block)
    {
        int index = BlockIndex(side);
        if (index >= 0 && index < _blocks.Count)
        {
            block = _blocks[index];
            return true;
        }

        block = null;
        return false;
    }

    /// <summary>Block under the active side's cursor, if the side has blocks.</summary>
    public bool TryGetBlock(out ChangeBlock? block) => TryGetBlock(_active, out block);

    /// <summary>
    /// Replaces the block collection, clamping both sides' block index and line
    /// cursor into range and clearing anchors that no longer point at a line.
    /// Returns true when any cursor or anchor was adjusted.
    /// </summary>
    public bool Replace(IReadOnlyList<ChangeBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        _blocks = blocks;

        bool changed = false;
        changed |= ClampSide(BlockSide.Left);
        changed |= ClampSide(BlockSide.Right);
        return changed;
    }

    /// <summary>Changes which side is active. Cursors and selections are not moved.</summary>
    public bool SetActive(BlockSide side)
    {
        if (_active == side)
        {
            return false;
        }

        _active = side;
        return true;
    }

    /// <summary>Flips the active side, the same operation the side-switch key is bound to.</summary>
    public bool SwitchActive() => SetActive(Other(_active));

    /// <summary>Moves the active side to another block, clearing its line anchor.</summary>
    public bool MoveBlock(int delta) => MoveBlock(_active, delta);

    /// <summary>
    /// Moves one side to another block by a signed delta, clamped to the block
    /// count. The line cursor is clamped to the new block and the anchor is
    /// cleared, because an anchored range may not span two blocks.
    /// </summary>
    public bool MoveBlock(BlockSide side, int delta)
    {
        int limit = Math.Max(0, _blocks.Count - 1);
        int current = BlockIndex(side);
        int next = (int)Math.Clamp((long)current + delta, 0, limit);
        if (next == current)
        {
            return false;
        }

        SetBlockAndLine(side, next, 0);
        SetAnchorValue(side, null);
        return true;
    }

    /// <summary>Moves the active side's line cursor by a signed delta.</summary>
    public bool MoveLine(int delta) => MoveLine(_active, delta);

    /// <summary>
    /// Moves one side's line cursor inside its current block, clamped to the
    /// block's line count. A clamp that lands on the current line is a no-op.
    /// </summary>
    public bool MoveLine(BlockSide side, int delta)
    {
        if (!TryGetBlock(side, out ChangeBlock? block) || block!.Lines.Count == 0)
        {
            return false;
        }

        int current = LineCursor(side);
        int next = (int)Math.Clamp((long)current + delta, 0, block.Lines.Count - 1);
        if (next == current)
        {
            return false;
        }

        SetLine(side, next);
        return true;
    }

    /// <summary>Starts a range selection at the active side's current line.</summary>
    public void SetAnchor() => SetAnchor(_active);

    /// <summary>Starts a range selection at one side's current line.</summary>
    public void SetAnchor(BlockSide side)
    {
        if (TryGetBlock(side, out ChangeBlock? block) && block!.Lines.Count > 0)
        {
            SetAnchorValue(side, LineCursor(side));
        }
    }

    /// <summary>Drops one side's anchor so its selection is the cursor line only.</summary>
    public bool ClearAnchor(BlockSide side)
    {
        int? anchor = Anchor(side);
        if (anchor is null)
        {
            return false;
        }

        SetAnchorValue(side, null);
        return true;
    }

    /// <summary>Drops the active side's anchor.</summary>
    public bool ClearAnchor() => ClearAnchor(_active);

    /// <summary>Selected line range on one side, always normalized low to high.</summary>
    public BlockLineRange SelectedRange(BlockSide side)
    {
        if (!TryGetBlock(side, out ChangeBlock? block) || block!.Lines.Count == 0)
        {
            return default;
        }

        int cursor = LineCursor(side);
        int? anchor = Anchor(side);
        if (anchor is null)
        {
            return new BlockLineRange(cursor, cursor + 1);
        }

        int low = Math.Min(anchor.Value, cursor);
        int high = Math.Max(anchor.Value, cursor);
        return new BlockLineRange(low, high + 1);
    }

    /// <summary>Selected line range on the active side.</summary>
    public BlockLineRange SelectedRange() => SelectedRange(_active);

    /// <summary>
    /// Reports the active side's selection as a transfer to the other side, or
    /// null when there is no block or an empty selection. State is unchanged.
    /// </summary>
    public BlockTransfer? Transfer() => Transfer(false);

    /// <summary>
    /// Reports a transfer from the active side, or from the other side when
    /// <paramref name="reverse"/> is set. The reported block and range come from
    /// the source side of the transfer; state is unchanged.
    /// </summary>
    public BlockTransfer? Transfer(bool reverse)
    {
        BlockSide from = reverse ? Other(_active) : _active;
        if (!TryGetBlock(from, out ChangeBlock? block))
        {
            return null;
        }

        BlockLineRange range = SelectedRange(from);
        if (range.IsEmpty)
        {
            return null;
        }

        return new BlockTransfer(block!.Id, range, from, Other(from));
    }

    /// <summary>
    /// Pure row projection for one side: a head with the block count, the
    /// focused block's label and a cursor-centred window of its lines. The same
    /// state and clip always produce the same rows.
    /// </summary>
    public IReadOnlyList<ChangeRow> Rows(BlockSide side, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<ChangeRow>();
        }

        bool active = side == _active;
        int cursor = LineCursor(side);
        BlockLineRange selection = SelectedRange(side);
        var rows = new List<ChangeRow>(height)
        {
            new ChangeRow(
                UnicodeTruncation.Truncate($"{side} ({_blocks.Count})", width, string.Empty),
                false,
                false,
                active),
        };

        if (!TryGetBlock(side, out ChangeBlock? block))
        {
            return rows;
        }

        ChangeBlock value = block!;
        rows.Add(new ChangeRow(
            UnicodeTruncation.Truncate(value.Label, width, UnicodeTruncation.Ellipsis),
            false,
            false,
            active));
        if (rows.Count >= height)
        {
            rows.RemoveRange(height, rows.Count - height);
            return rows;
        }

        IReadOnlyList<ChangeLine> lines = value.Lines;
        if (lines.Count == 0)
        {
            return rows;
        }

        int bodyRows = height - rows.Count;
        int start = lines.Count <= bodyRows
            ? 0
            : Math.Clamp(cursor - (bodyRows / 2), 0, lines.Count - bodyRows);
        for (int i = start; i < lines.Count && rows.Count < height; i++)
        {
            ChangeLine line = lines[i];
            string prefix = i == cursor ? "> " : (selection.Contains(i) ? "* " : "  ");
            rows.Add(new ChangeRow(
                UnicodeTruncation.Truncate(prefix + line.Number + " " + line.Text, width, UnicodeTruncation.Ellipsis),
                i == cursor,
                selection.Contains(i),
                active));
        }

        return rows;
    }

    /// <summary>Pure row projection for the active side.</summary>
    public IReadOnlyList<ChangeRow> Rows(int width, int height) => Rows(_active, width, height);

    /// <summary>
    /// Paints both sides inside <paramref name="clip"/>. A one-cell divider is
    /// reserved when the clip is wide enough; the active side keeps
    /// <paramref name="activeStyle"/>, the other uses
    /// <paramref name="inactiveStyle"/>. Selection rows are underlined and the
    /// cursor row is reversed.
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

        PaintSide(
            buffer,
            new Rect(clip.X, clip.Y, leftWidth, clip.Height),
            BlockSide.Left,
            activeStyle,
            inactiveStyle);
        PaintSide(
            buffer,
            new Rect(clip.X + leftWidth + gap, clip.Y, rightWidth, clip.Height),
            BlockSide.Right,
            activeStyle,
            inactiveStyle);
    }

    private void PaintSide(
        ScreenBuffer buffer,
        Rect rect,
        BlockSide side,
        CellStyle activeStyle,
        CellStyle inactiveStyle)
    {
        IReadOnlyList<ChangeRow> rows = Rows(side, rect.Width, rect.Height);
        CellStyle sideStyle = side == _active ? activeStyle : inactiveStyle;
        var cursorStyle = new CellStyle(attrs: StyleAttr.Reverse);
        var selectedStyle = new CellStyle(attrs: StyleAttr.Underline);

        for (int i = 0; i < rows.Count; i++)
        {
            ChangeRow row = rows[i];
            CellStyle style = row.IsCursor
                ? cursorStyle
                : (row.IsSelected ? selectedStyle : sideStyle);
            buffer.SetText(rect.X, rect.Y + i, row.Text.AsSpan(), style);
        }
    }

    private bool ClampSide(BlockSide side)
    {
        int limit = Math.Max(0, _blocks.Count - 1);
        int blockIndex = (int)Math.Clamp(BlockIndex(side), 0, limit);
        int line = 0;
        if (_blocks.Count > 0)
        {
            int lineLimit = Math.Max(0, _blocks[blockIndex].Lines.Count - 1);
            line = (int)Math.Clamp(LineCursor(side), 0, lineLimit);
        }

        bool changed = blockIndex != BlockIndex(side) || line != LineCursor(side);
        SetBlockAndLine(side, blockIndex, line);

        int? anchor = Anchor(side);
        if (anchor is not null)
        {
            int anchorLimit = _blocks.Count > 0 ? Math.Max(0, _blocks[blockIndex].Lines.Count - 1) : -1;
            if (anchor.Value > anchorLimit)
            {
                SetAnchorValue(side, null);
                changed = true;
            }
        }

        return changed;
    }

    private void SetBlockAndLine(BlockSide side, int blockIndex, int line)
    {
        if (side == BlockSide.Left)
        {
            _leftBlock = blockIndex;
            _leftLine = line;
        }
        else
        {
            _rightBlock = blockIndex;
            _rightLine = line;
        }
    }

    private void SetLine(BlockSide side, int line)
    {
        if (side == BlockSide.Left)
        {
            _leftLine = line;
        }
        else
        {
            _rightLine = line;
        }
    }

    private void SetAnchorValue(BlockSide side, int? anchor)
    {
        if (side == BlockSide.Left)
        {
            _leftAnchor = anchor;
        }
        else
        {
            _rightAnchor = anchor;
        }
    }

    private static BlockSide Other(BlockSide side) => side == BlockSide.Left ? BlockSide.Right : BlockSide.Left;
}
