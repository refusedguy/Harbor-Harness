namespace Harbor.Tui.CellForge.Rendering;

using System.Buffers;
using Harbor.Ui.Framework.Rendering;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Ui.Framework.Rendering.Protocol;

// #436: this adapter straddles the engine/host boundary inside the engine's
// namespace, so bare cell/screen names resolve to the engine's verbatim
// ports. The portable protocol (ICellDiffEncoder/RowHashDiffEncoder) speaks
// the Rendering vocabulary, while DiffEngine.Front is engine-typed — both
// sides are pinned explicitly (EngineCells vs UIR) and the front crosses
// the boundary through a field-for-field snapshot (the reverse of
// ScreenSession.CopyPaintToBack; both cell structs share the 16-byte
// layout, so every field round-trips).
using EngineCells = Harbor.Tui.CellForge.Rendering;
using UIR = Harbor.Ui.Framework.Rendering;

/// <summary>
///     CellForge-side factory for the portable cell-diff protocol
///     (renderer-unification sprint Phase 6.2). The single
///     <see cref="ICellDiffEncoder" /> implementation is
///     <see cref="RowHashDiffEncoder" /> — this factory only binds it to a
///     <see cref="DiffEngine" />'s already-shown front buffer for the
///     engine-linked mode, without touching the ANSI path: the
///     <see cref="DiffEngine"/> SGR automaton stays untouched (hard rule 5);
///     the encoder reads the engine's front through its public surface and
///     encodes the delta against it.
/// </summary>
/// <remarks>
///     <para>
///         Engine-linked mode (recommended for CellForge consumers): after
///         each <see cref="DiffEngine.Flush"/>, the engine's front equals the
///         frame the terminal received. Encoding
///         <c>engine front → next frame</c> therefore yields exactly the
///         batch a parallel differential consumer needs to stay in sync with
///         what the ANSI path painted — e.g. SpectreTui panels, Blazor DOM
///         mirrors, or a remote-process renderer.
///     </para>
/// </remarks>
public static class CellForgeDiffEncoder
{
    /// <summary>Standalone encoder — callers supply both frames.</summary>
    public static ICellDiffEncoder Create() => new RowHashDiffEncoder();

    // Static helper runs on the frame path: RowHashDiffEncoder retains its
    // staging array across Encode calls (amortized, see its remarks), so a
    // fresh instance per flush re-grows the scratch every frame. Reused
    // per thread — Encode mutates staging, hence ThreadStatic, not shared.
    [ThreadStatic]
    private static RowHashDiffEncoder? _cachedEncoder;

    /// <summary>
    ///     Engine-linked encoder — diffs against the engine's current front
    ///     buffer (the last frame it flushed).
    /// </summary>
    public static EngineLinkedEncoder CreateEngineLinked(DiffEngine engine) =>
        new(engine ?? throw new ArgumentNullException(nameof(engine)));

    /// <summary>
    ///     Encodes the delta from <paramref name="engine"/>'s front buffer
    ///     (the last frame the ANSI path flushed) to <paramref name="next"/>.
    ///     The front crosses into the Rendering vocabulary through a snapshot
    ///     copy; the portable encoder then diffs two Rendering buffers.
    /// </summary>
    public static CellDiffBatch EncodeFromEngineFront(
        EngineCells.DiffEngine engine,
        UIR.ScreenBuffer next,
        IReadOnlyList<UIR.Rect>? hints,
        long sequence)
    {
        Ensure.NotNull(engine);
        Ensure.NotNull(next);
        var prev = SnapshotFront(engine.Front);
        _cachedEncoder ??= new RowHashDiffEncoder();
        return _cachedEncoder.Encode(prev, next, hints, sequence);
    }

    /// <summary>
    ///     Snapshots an engine front buffer into the Rendering vocabulary:
    ///     exact cell-for-cell copy, then row-hash invalidation so the
    ///     portable encoder rehashes what the copy touched (At() writes do
    ///     not invalidate, same discipline as
    ///     <see cref="Harbor.Tui.CellForge.Streaming.ScreenSession"/>).
    /// </summary>
    private static UIR.ScreenBuffer SnapshotFront(EngineCells.ScreenBuffer front)
    {
        var snap = new UIR.ScreenBuffer(front.Cols, front.Rows);
        CopyEngineToRendering(front, snap);
        return snap;
    }

    private static void CopyEngineToRendering(EngineCells.ScreenBuffer src, UIR.ScreenBuffer dst)
    {
        for (int y = 0; y < src.Rows; y++)
        {
            for (int x = 0; x < src.Cols; x++)
            {
                var c = src.Get(x, y);
                dst.At(x, y) = UIR.Cell.FromRaw(c.Rune, c.Fg, c.Bg, c.Flags, c.Width);
                // R1 steal: the directive table crosses with the cells — both
                // enums are byte-backed with identical members in identical
                // order, so the numeric cast is exact. Copied per cell (not
                // just when non-None): the snapshot buffer is retained across
                // calls and a stale Skip would hide the next frame.
                dst.SetDiffOption(x, y, (UIR.CellDiffOption)(byte)src.GetDiffOption(x, y), src.GetForcedWidth(x, y));
            }
        }

        dst.InvalidateAll();
    }

    /// <summary>
    ///     <see cref="ICellDiffEncoder" /> bound to a <see cref="DiffEngine" />'s
    ///     front buffer. <see cref="EncodeCellForge" /> diffs the engine's
    ///     last-flushed frame against <c>next</c>; <see cref="Encode" /> is the
    ///     plain explicit-pair contract (delegated to the shared portable
    ///     encoder).
    /// </summary>
    public sealed class EngineLinkedEncoder : ICellDiffEncoder
    {
        private readonly EngineCells.DiffEngine _engine;
        private readonly RowHashDiffEncoder _portable = new();
        private UIR.ScreenBuffer _snapshot = new(0, 0);

        public EngineLinkedEncoder(EngineCells.DiffEngine engine) =>
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        /// <inheritdoc />
        public CellDiffBatch Encode(
            UIR.ScreenBuffer prev,
            UIR.ScreenBuffer next,
            IReadOnlyList<UIR.Rect>? hints,
            long sequence)
        {
            return _portable.Encode(prev, next, hints, sequence);
        }

        /// <summary>
        ///     Encodes the delta from the linked engine's front buffer (the last
        ///     frame the ANSI path flushed) to <paramref name="next"/>. The
        ///     snapshot buffer is retained across calls and only regrown, so
        ///     the steady-state cost is the copy itself, never an allocation.
        /// </summary>
        public CellDiffBatch EncodeCellForge(
            UIR.ScreenBuffer next,
            IReadOnlyList<UIR.Rect>? hints,
            long sequence)
        {
            Ensure.NotNull(next);
            SyncSnapshot();
            return _portable.Encode(_snapshot, next, hints, sequence);
        }

        private void SyncSnapshot()
        {
            var front = _engine.Front;
            if (_snapshot.Cols != front.Cols || _snapshot.Rows != front.Rows)
            {
                _snapshot = new UIR.ScreenBuffer(front.Cols, front.Rows);
            }

            CopyEngineToRendering(front, _snapshot);
        }
    }
}

/// <summary>
///     One hunk after hunk virtualization (O5 steal, epic #1155, issue #1174 —
///     port of opencode <c>util/diff.ts splitPatchHunks</c> + <c>patch-diff.tsx</c>).
/// </summary>
/// <param name="Header">
///     The <c>@@</c> header this hunk renders under: the original header for the
///     first segment, a synthesized one (with full-file line numbers and a
///     <c>chunk k/n</c> marker) for every segment cut from it.
/// </param>
/// <param name="Lines">Raw unified-diff content lines (<c>+</c>/<c>-</c>/<c> </c> prefixes kept).</param>
/// <param name="IsVirtualChunk">
///     True when this hunk was cut out of a longer added-only run, rather than
///     passed through.
/// </param>
/// <param name="ChunkIndex">One-based index within the source hunk (1 when passed through).</param>
/// <param name="ChunkCount">How many hunks the source hunk became (1 when passed through).</param>
/// <param name="OldStart">Old-file start line of this (chunk-)hunk.</param>
/// <param name="NewStart">New-file start line of this (chunk-)hunk.</param>
public sealed record VirtualHunk(
    string Header,
    IReadOnlyList<string> Lines,
    bool IsVirtualChunk,
    int ChunkIndex,
    int ChunkCount,
    int OldStart,
    int NewStart);

/// <summary>A patch after hunk virtualization.</summary>
/// <param name="Preamble"><c>---</c>/<c>+++</c> lines above the first hunk (never cut).</param>
/// <param name="Hunks">Hunks in diff order (chunked where cut).</param>
/// <param name="TotalLines">Content lines across all hunks (chunking preserves the count).</param>
/// <param name="MaxNewLine">
///     Full-file maximum new-side line number — the gutter-width anchor. Taken
///     over the whole patch, never per chunk, so chunk gutters never jitter.
/// </param>
/// <param name="WasChunked">True when at least one added-only run was cut.</param>
public sealed record VirtualPatch(
    IReadOnlyList<string> Preamble,
    IReadOnlyList<VirtualHunk> Hunks,
    int TotalLines,
    int MaxNewLine,
    bool WasChunked);

/// <summary>
///     Hunk-virtualized patch splitter (O5 steal, epic #1155, issue #1174):
///     the <c>util/diff.ts splitPatchHunks</c> + <c>patch-diff.tsx</c> rules —
///     virtualize at <see cref="VirtualMinLines"/>, cut only added-only runs
///     into <see cref="VirtualChunkLines"/>-line chunks with their own
///     <c>@@</c> headers, size the gutter from the full file, and pick
///     split/unified automatically above <see cref="SplitWidthThreshold"/>.
/// </summary>
/// <remarks>
///     Pure BCL, no <c>LineDiff</c> engine calls: this file stays outside the
///     diff-surface projection perimeter by construction (see
///     <c>DiffSurfaceNameCollisionRule</c>). CRLF is normalized up front, so a
///     Windows-authored patch never diffs as fully rewritten (#570).
/// </remarks>
public static class HunkVirtualizer
{
    /// <summary>
    ///     Below this many content lines a patch renders whole; at or above it
    ///     the panel renders a head window with a virtualization marker
    ///     (<c>patch-diff.tsx VIRTUAL_MIN_LINES</c>).
    /// </summary>
    public const int VirtualMinLines = 3000;

    /// <summary>
    ///     Added-only runs longer than this are cut into chunks of at most this
    ///     many lines (<c>patch-diff.tsx VIRTUAL_CHUNK_LINES</c>). Runs with
    ///     context or removed lines mixed in are never cut.
    /// </summary>
    public const int VirtualChunkLines = 128;

    /// <summary>
    ///     Above this width the diff renders split (side-by-side), at or below
    ///     it unified (<c>patch-diff.tsx</c> split/unified auto rule).
    /// </summary>
    public const int SplitWidthThreshold = 120;

    /// <summary>True when a body with this many lines must render virtualized.</summary>
    public static bool NeedsVirtualization(int totalLines) => totalLines >= VirtualMinLines;

    /// <summary>True when this width renders split, false for unified.</summary>
    public static bool PreferSplit(int width) => width > SplitWidthThreshold;

    /// <summary>Decimal width of a full-file line number (the stable gutter).</summary>
    public static int GutterWidth(int maxLine)
    {
        int width = 1;
        for (int n = Math.Max(0, maxLine); n >= 10; n /= 10)
        {
            width++;
        }

        return width;
    }

    /// <summary>
    ///     Splits a unified patch into hunks, cutting only added-only runs
    ///     longer than <see cref="VirtualChunkLines"/> into chunks with their
    ///     own <c>@@</c> headers carrying full-file line numbers. Everything
    ///     else — preamble, mixed hunks, short runs — passes through untouched.
    /// </summary>
    public static VirtualPatch SplitPatchHunks(string? patchText)
    {
        // #1139: the old shape materialized the patch TWICE (a "\r\n" → "\n"
        // Replace and a lone-'\r' → "\n" Replace over the whole body), split
        // the result into a string[] (one string per line), and then Replace'd
        // every tab in every line — four copies of the body, on a path the
        // diff-preview panel runs on EVERY FRAME it is visible. This shape
        // walks the body once: the breaks are found in place (CRLF is one
        // break, a lone CR is one too — the same breaks the old normalization
        // produced), the trailing empty line is still not a line, and a line
        // is materialized once, with its tabs expanded.
        string text = patchText ?? string.Empty;
        var preamble = new List<string>();
        var hunks = new List<VirtualHunk>();
        List<string>? current = null;
        string currentHeader = string.Empty;
        int currentOld = 1;
        int currentNew = 1;
        int nextOld = 1;
        int nextNew = 1;
        int total = 0;
        int maxNew = 1;
        bool chunked = false;

        void Flush()
        {
            if (current is null)
            {
                return;
            }

            FlushHunk(current, currentHeader, currentOld, currentNew, hunks, ref maxNew, ref chunked, out nextOld, out nextNew);
            current = null;
        }

        void Take(string line)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                current = new List<string>();
                currentHeader = line;
                if (TryParseHunkHeader(line, out int oldStart, out int newStart))
                {
                    currentOld = oldStart;
                    currentNew = newStart;
                }
                else
                {
                    currentOld = nextOld;
                    currentNew = nextNew;
                }

                return;
            }

            if (current is null)
            {
                preamble.Add(line);
            }
            else
            {
                current.Add(line);
                total++;
            }
        }

        // The last line of the body is not a line when it is empty (the
        // trailing newline of a patch body) — the old shape dropped that one
        // element from the split array. The walk therefore holds one line
        // back and decides at the end, which is the same rule.
        int pos = 0;
        int pendingStart = 0;
        int pendingLength = -1;

        while (true)
        {
            int breakIndex = -1;
            int nextPos = text.Length;
            for (int i = pos; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    breakIndex = i;
                    nextPos = i + 1;
                    break;
                }

                if (c == '\r')
                {
                    breakIndex = i;
                    nextPos = i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                    break;
                }
            }

            int length = breakIndex < 0 ? text.Length - pos : breakIndex - pos;
            if (pendingLength >= 0)
            {
                Take(ExpandTabs(text.AsSpan(pendingStart, pendingLength)));
            }

            pendingStart = pos;
            pendingLength = length;

            if (breakIndex < 0)
            {
                break;
            }

            pos = nextPos;
        }

        if (pendingLength > 0)
        {
            Take(ExpandTabs(text.AsSpan(pendingStart, pendingLength)));
        }

        Flush();

        if (hunks.Count == 0 && total == 0 && preamble.Count > 0)
        {
            // Headerless body: the preamble IS the content (mirrors the
            // DiffViewerOverlay single-synthesized-hunk fallback).
            var single = new VirtualHunk(string.Empty, preamble.ToArray(), false, 1, 1, 1, 1);
            return new VirtualPatch(Array.Empty<string>(), [single], preamble.Count, Math.Max(1, preamble.Count), false);
        }

        return new VirtualPatch(preamble.ToArray(), hunks.ToArray(), total, maxNew, chunked);
    }

    /// <summary>
    ///     One line of a patch body with every tab expanded to two spaces —
    ///     the old shape's per-line <c>Replace("\t", "  ")</c>, materialized
    ///     once instead of once per line plus once per tabbed line.
    /// </summary>
    /// <remarks>
    ///     The expansion is written into a POOLED buffer and handed to the
    ///     string constructor, so a tabbed line costs ONE allocation (the
    ///     string). The first cut of this slice built the line in a
    ///     <see cref="System.Text.StringBuilder" /> instead, and the CI measurement of
    ///     that cut read **208 936 B per split against the old shape's
    ///     175 248 B** — a StringBuilder per line costs more than the
    ///     Replace it replaced (the object, its chunk buffer and the string
    ///     it hands back), which is exactly the kind of number this slice
    ///     exists to remove.
    /// </remarks>
    private static string ExpandTabs(ReadOnlySpan<char> line)
    {
        int firstTab = line.IndexOf('\t');
        if (firstTab < 0)
        {
            return line.ToString();
        }

        int tabs = 0;
        for (int i = firstTab; i < line.Length; i++)
        {
            if (line[i] == '\t')
            {
                tabs++;
            }
        }

        char[] buffer = ArrayPool<char>.Shared.Rent(line.Length + tabs);
        try
        {
            int n = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\t')
                {
                    buffer[n++] = ' ';
                    buffer[n++] = ' ';
                }
                else
                {
                    buffer[n++] = c;
                }
            }

            return new string(buffer, 0, n);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void FlushHunk(
        List<string> lines,
        string header,
        int oldStart,
        int newStart,
        List<VirtualHunk> into,
        ref int maxNew,
        ref bool chunked,
        out int endOld,
        out int endNew)
    {
        int old = oldStart;
        int nw = newStart;
        var segment = new List<string>();
        int segmentOld = old;
        int segmentNew = nw;
        bool first = true;

        // A hunk that survives whole keeps its original header; any hunk that
        // is cut synthesizes every header (even the leading remainder's — its
        // counts no longer match the original).
        bool whole = true;
        for (int s = 0; s < lines.Count;)
        {
            int scan = AddedRunLength(lines, s);
            if (scan > VirtualChunkLines)
            {
                whole = false;
                break;
            }

            s += Math.Max(1, scan);
        }

        int i = 0;

        void EmitSegment(bool isChunk, int chunkIndex, int chunkCount, int oldCount, int newCount)
        {
            string segmentHeader = first && whole
                ? header
                : SynthHeader(segmentOld, oldCount, segmentNew, newCount, chunkIndex, chunkCount);
            into.Add(new VirtualHunk(
                segmentHeader,
                segment.ToArray(),
                isChunk,
                chunkCount > 1 ? chunkIndex : 1,
                Math.Max(1, chunkCount),
                segmentOld,
                segmentNew));
            first = false;
        }

        while (i < lines.Count)
        {
            int run = AddedRunLength(lines, i);
            if (run > VirtualChunkLines)
            {
                if (segment.Count > 0)
                {
                    EmitSegment(isChunk: false, chunkIndex: 0, chunkCount: 1, old - segmentOld, nw - segmentNew);
                    segment.Clear();
                }

                int chunks = (run + VirtualChunkLines - 1) / VirtualChunkLines;
                for (int k = 0; k < chunks; k++)
                {
                    int take = Math.Min(VirtualChunkLines, run - (k * VirtualChunkLines));
                    segment.AddRange(lines.GetRange(i + (k * VirtualChunkLines), take));
                    EmitSegment(isChunk: true, chunkIndex: k + 1, chunkCount: chunks, oldCount: 0, newCount: take);
                    segment.Clear();
                    nw += take;
                }

                chunked = true;
                segmentOld = old;
                segmentNew = nw;
                i += run;
            }
            else
            {
                string line = lines[i];
                if (IsAdded(line))
                {
                    nw++;
                }
                else if (IsRemoved(line))
                {
                    old++;
                }
                else
                {
                    old++;
                    nw++;
                }

                segment.Add(line);
                i++;
            }
        }

        if (segment.Count > 0)
        {
            EmitSegment(isChunk: false, chunkIndex: 0, chunkCount: 1, old - segmentOld, nw - segmentNew);
        }

        endOld = old;
        endNew = nw;
        if (lines.Count > 0)
        {
            maxNew = Math.Max(maxNew, Math.Max(newStart, nw - 1));
        }
        else
        {
            maxNew = Math.Max(maxNew, newStart);
        }
    }

    private static string SynthHeader(int oldStart, int oldCount, int newStart, int newCount, int chunkIndex, int chunkCount)
    {
        string header = "@@ -" + oldStart + "," + oldCount + " +" + newStart + "," + newCount + " @@";
        return chunkCount > 1 ? header + " chunk " + chunkIndex + "/" + chunkCount : header;
    }

    private static int AddedRunLength(List<string> lines, int start)
    {
        int run = 0;
        while (start + run < lines.Count && IsAdded(lines[start + run]))
        {
            run++;
        }

        return run;
    }

    private static bool IsAdded(string line) => line.Length > 0 && line[0] == '+';

    private static bool IsRemoved(string line) => line.Length > 0 && line[0] == '-';

    private static bool TryParseHunkHeader(string line, out int oldStart, out int newStart)
    {
        oldStart = 1;
        newStart = 1;
        int dash = line.IndexOf('-');
        int plus = line.IndexOf('+');
        if (dash < 0 || plus < 0 || plus < dash)
        {
            return false;
        }

        return TryReadNumber(line, dash + 1, out oldStart) && TryReadNumber(line, plus + 1, out newStart);
    }

    private static bool TryReadNumber(string text, int index, out int value)
    {
        value = 0;
        int digits = 0;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            value = (value * 10) + (text[index] - '0');
            index++;
            digits++;
        }

        return digits > 0;
    }
}
