namespace Harbor.Ui.Framework.Rendering.Markdown;

using System.Text;

using System.Collections.Immutable;
using Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     Differential markdown pipeline (renderer-unification sprint Phase
///     6.4): bridges the shared <see cref="StreamingMarkdownRenderer"/>
///     frozen-tail algorithm and the portable cell-diff protocol, giving every
///     renderer backend <b>O(1) re-render cost for completed blocks</b> — the
///     tail is the only thing that ever gets re-styled and re-diffed.
/// </summary>
/// <remarks>
///     <para>
///         Completed blocks freeze into the <see cref="Cache"/> as immutable
///         cell snapshots; the active tail block re-renders per token and its
///         cells flow through <see cref="DifferentialRenderPipeline"/>, so the
///         emitted <see cref="CellDiffBatch"/> contains tail-only changes.
///         Consumers (SpectreTui, Blazor DOM, remote renderers) subscribe as
///         <see cref="ICellDiffSink"/>s on <see cref="Diffs"/>.
///     </para>
///     <para>
///         Style mapping: <see cref="MdStyle"/> → <see cref="CellStyle"/> via
///         SGR attribute bits; <see cref="MdStyle.Code"/> additionally renders
///         on a dim background, <see cref="MdStyle.Heading"/> bold+dim, and
///         <see cref="MdStyle.Fence"/> dim.
///     </para>
/// </remarks>
public sealed class DifferentialMarkdownPipeline
{
    private readonly DifferentialRenderPipeline _diff;
    private readonly FrozenTailMarkdownCache _cache;
    private readonly ScreenBuffer _screen;
    private readonly Dictionary<int, int> _blockRows; // blockId → top row
    private int _lastTailRow;

    public DifferentialMarkdownPipeline(int cols, int rows)
    {
        _diff = new DifferentialRenderPipeline();
        _cache = new FrozenTailMarkdownCache();
        _screen = new ScreenBuffer(cols, rows);
        _blockRows = new Dictionary<int, int>();
    }

    /// <summary>Subscribe backend sinks here to receive tail-only cell diffs.</summary>
    public DifferentialRenderPipeline Diffs => _diff;

    /// <summary>The frozen-block snapshot cache (LRU, observable).</summary>
    public FrozenTailMarkdownCache Cache => _cache;

    /// <summary>Height of the compositing screen.</summary>
    public int Rows => _screen.Rows;

    /// <summary>
    ///     Renders one markdown block at row <paramref name="y"/>: a frozen
    ///     block is restored from the cache (O(1) copy), an incomplete tail
    ///     block is re-styled from its spans, and a just-completed block
    ///     freezes after its first (final) render. The returned batch carries
    ///     the changed cells for the block's rows only.
    /// </summary>
    /// <param name="blockId">Stable identity of the markdown block.</param>
    /// <param name="lines">Display lines of the block (already wrapped by the caller).</param>
    /// <param name="isComplete">Whether the block is finished streaming.</param>
    /// <param name="y">Top row of the block on the compositing screen.</param>
    public CellDiffBatch RenderBlock(int blockId, IReadOnlyList<MdLine> lines, bool isComplete, int y)
    {
        int height = Math.Min(lines.Count, Math.Max(0, _screen.Rows - y));
        if (height <= 0)
        {
            return RenderEmptyAt(y);
        }

        bool restored = false;
        if (isComplete && _cache.TryGet(blockId, out Cell[]? frozen) && frozen.Length == height * _screen.Cols)
        {
            BlitFrozen(frozen, y, height);
            restored = true;
        }

        if (!restored)
        {
            for (int row = 0; row < height; row++)
            {
                RenderSpans(lines[row], y + row);
            }
        }

        _blockRows[blockId] = y;
        _lastTailRow = y + height - 1;

        if (isComplete && !restored)
        {
            _cache.Freeze(blockId, CopyBlock(y, height));
        }

        // Damage rect = this block's rows: the row-hash fast path skips the
        // rest of the screen, so the batch is tail-only by construction.
        var hints = new[] { new Rect(0, y, _screen.Cols, height) };
        return _diff.Render(_screen, hints);
    }

    /// <summary>
    ///     Restores a previously frozen block (theme/resize/session restore
    ///     path) — the documented &lt;1 ms per block guarantee.
    /// </summary>
    public CellDiffBatch RestoreFrozenBlock(int blockId, int y, int height)
    {
        if (!_cache.TryGet(blockId, out Cell[]? frozen) || frozen.Length != height * _screen.Cols)
        {
            throw new InvalidOperationException(
                $"Block {blockId} is not frozen with the requested geometry ({height} rows).");
        }

        BlitFrozen(frozen, y, height);
        var hints = new[] { new Rect(0, y, _screen.Cols, height) };
        return _diff.Render(_screen, hints);
    }

    /// <summary>Drops all frozen state (resize/theme change): next renders are full repaints.</summary>
    public void InvalidateAll()
    {
        _cache.Clear();
        _blockRows.Clear();
        _diff.Reset();
    }

    private void RenderSpans(MdLine line, int row)
    {
        // Clear the row first: an incomplete tail can shrink between pushes.
        for (int x = 0; x < _screen.Cols; x++)
        {
            _screen.At(x, row) = Cell.Blank;
        }

        int cursor = 0;
        IReadOnlyList<MdSpan> spans = line.Spans;
        for (int i = 0; i < spans.Count && cursor < _screen.Cols; i++)
        {
            MdSpan span = spans[i];
            CellStyle style = StyleFor(span.Style);
            string text = span.Text;
            for (int c = 0; c < text.Length && cursor < _screen.Cols; c++)
            {
                _screen.SetRune(cursor++, row, new Rune(text[c]), style);
            }
        }
    }

    private void BlitFrozen(Cell[] frozen, int y, int height)
    {
        int cols = _screen.Cols;
        for (int row = 0; row < height; row++)
        {
            int offset = row * cols;
            for (int x = 0; x < cols; x++)
            {
                _screen.At(x, y + row) = frozen[offset + x];
            }

            _screen.MarkRowDirty(y + row);
        }
    }

    private Cell[] CopyBlock(int y, int height)
    {
        int cols = _screen.Cols;
        var snapshot = new Cell[height * cols];
        for (int row = 0; row < height; row++)
        {
            for (int x = 0; x < cols; x++)
            {
                snapshot[(row * cols) + x] = _screen.Get(x, y + row);
            }
        }

        return snapshot;
    }

    private CellDiffBatch RenderEmptyAt(int y)
    {
        if (y < _screen.Rows)
        {
            for (int x = 0; x < _screen.Cols; x++)
            {
                _screen.At(x, y) = Cell.Blank;
            }

            _screen.MarkRowDirty(y);
        }

        return _diff.Render(_screen, new[] { new Rect(0, y, _screen.Cols, 1) });
    }

    internal static CellStyle StyleFor(MdStyle style) => style switch
    {
        MdStyle.Bold => new CellStyle(PackedColor.Default, PackedColor.Default, StyleAttr.Bold),
        MdStyle.Italic => new CellStyle(PackedColor.Default, PackedColor.Default, StyleAttr.Italic),
        MdStyle.BoldItalic => new CellStyle(PackedColor.Default, PackedColor.Default, StyleAttr.Bold | StyleAttr.Italic),
        MdStyle.Code => new CellStyle(
            PackedColor.Default,
            PackedColor.Rgb(40, 40, 40),
            StyleAttr.None),
        MdStyle.Heading => new CellStyle(PackedColor.Default, PackedColor.Default, StyleAttr.Bold | StyleAttr.Underline),
        MdStyle.Fence => new CellStyle(PackedColor.Default, PackedColor.Default, StyleAttr.Dim),
        _ => CellStyle.Plain,
    };
}
