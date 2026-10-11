using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     DiffPlex pilot for issue #1236: line-level and word-level diffs computed by
///     the DiffPlex engine but returned in the existing
///     <see cref="LineDiff"/> row shapes, so every current caller keeps compiling
///     and the default rendering path is untouched.
/// </summary>
/// <remarks>
///     <para>
///         Strictly outside the CellForge engine: this lives in the State layer next
///         to <see cref="DiffPreview"/> (diff is a chat and panel concern), and no
///         file under the engine is referenced or modified.
///     </para>
///     <para>
///         Opt-in only. Nothing here runs unless <see cref="IsEnabled"/> reads true,
///         so the gate stays green by construction: with the flag unset every preview
///         renders exactly what it rendered before this file existed.
///     </para>
///     <para>
///         A pilot, not a migration. The existing matchers stay where they are; this
///         file only proves the external engine behind the same API. Rollback leaves
///         no traces: delete this file, revert the two gated call sites (desktop
///         diff view-model and <see cref="DiffPreview"/>), and drop the DiffPlex
///         package entries. No engine file names this type.
///     </para>
/// </remarks>
public static class DiffPlexPilot
{
    /// <summary>Environment flag that opts the desktop and patch previews into the pilot.</summary>
    public const string EnableEnvVar = "HARBOR_DIFFPLEX";

    /// <summary>
    ///     Largest combined input, in characters, the pilot will diff. Past it the
    ///     call degrades to false and the caller keeps the existing rendering —
    ///     the pilot must never turn a large tool result into a stall.
    /// </summary>
    public const int MaxPilotInputChars = 256 * 1024;

    /// <summary>True when the pilot flag carries a truthy value.</summary>
    public static bool IsEnabled
    {
        get
        {
            string? flag = Environment.GetEnvironmentVariable(EnableEnvVar);
            return string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Line-level diff of <paramref name="before"/> against
    ///     <paramref name="after"/>, in the same row shape
    ///     <see cref="LineDiff.Compute"/> returns: unchanged rows carry both line
    ///     numbers, additions carry only the new one, removals only the old one.
    /// </summary>
    /// <param name="before">Old text. Null is an empty text.</param>
    /// <param name="after">New text. Null is an empty text.</param>
    /// <param name="rows">The mapped rows, or an empty list on degradation.</param>
    /// <returns>True when the pilot diffed; false when the caller should fall back.</returns>
    public static bool TryComputeRows(string? before, string? after, out IReadOnlyList<LineDiffRow> rows)
    {
        rows = [];
        if (InputTooLarge(before, after))
        {
            return false;
        }

        try
        {
            DiffPaneModel model = InlineDiffBuilder.Diff(
                before ?? string.Empty,
                after ?? string.Empty,
                ignoreWhiteSpace: false,
                ignoreCase: false);

            var mapped = new List<LineDiffRow>(model.Lines.Count);
            int oldNo = 1;
            int newNo = 1;
            for (int i = 0; i < model.Lines.Count; i++)
            {
                DiffPiece piece = model.Lines[i];
                string text = piece.Text ?? string.Empty;
                switch (piece.Type)
                {
                    case ChangeType.Unchanged:
                        mapped.Add(new LineDiffRow(LineDiffRowKind.Unchanged, oldNo++, newNo++, text));
                        break;
                    case ChangeType.Deleted:
                        mapped.Add(new LineDiffRow(LineDiffRowKind.Removed, oldNo++, 0, text));
                        break;
                    case ChangeType.Inserted:
                        mapped.Add(new LineDiffRow(LineDiffRowKind.Added, 0, newNo++, text));
                        break;
                    default:
                        // The inline builder only emits the three kinds above; a
                        // future piece kind degrades to a rewrite pair rather
                        // than dropping a line.
                        mapped.Add(new LineDiffRow(LineDiffRowKind.Removed, oldNo++, 0, text));
                        mapped.Add(new LineDiffRow(LineDiffRowKind.Added, 0, newNo++, text));
                        break;
                }
            }

            rows = mapped;
            return true;
        }
        catch (Exception)
        {
            // Pilot invariant: never break a preview. The state layer already
            // holds this tolerance for hosted effects (TuiEffectHost).
            rows = [];
            return false;
        }
    }

    /// <summary>
    ///     The pilot rows rendered in the context-diff block the core speaks, via
    ///     <see cref="LineDiff.ToUnifiedText"/>. Null when the pilot degrades.
    /// </summary>
    /// <param name="before">Old text. Null is an empty text.</param>
    /// <param name="after">New text. Null is an empty text.</param>
    /// <param name="unified">The rendered block, or null on degradation.</param>
    /// <returns>True when the pilot rendered; false when the caller should fall back.</returns>
    public static bool TryComputeUnified(string? before, string? after, out string? unified)
    {
        if (TryComputeRows(before, after, out IReadOnlyList<LineDiffRow> rows))
        {
            unified = LineDiff.ToUnifiedText(rows);
            return true;
        }

        unified = null;
        return false;
    }

    /// <summary>
    ///     Pilot preview for the patch surface: the unified rendering above, cut to
    ///     <paramref name="maxLines"/> with the same overflow marker the existing
    ///     preview uses. Null when the pilot degrades.
    /// </summary>
    /// <param name="before">Old text. Null is an empty text.</param>
    /// <param name="after">New text. Null is an empty text.</param>
    /// <param name="maxLines">Visible line budget; must be positive.</param>
    /// <param name="preview">The cut preview, or null on degradation.</param>
    /// <returns>True when the pilot rendered; false when the caller should fall back.</returns>
    public static bool TryComputePreview(string? before, string? after, int maxLines, out string? preview)
    {
        preview = null;
        if (maxLines <= 0)
        {
            return false;
        }

        if (!TryComputeUnified(before, after, out string? unified) || unified is null)
        {
            return false;
        }

        preview = TruncateToLines(unified, maxLines);
        return true;
    }

    /// <summary>
    ///     Word-level segmentation of one removed line against its replacement (the
    ///     bonus half of the pilot), projected onto the shared
    ///     <see cref="WordDiffSides"/> shape: equal runs anchor both rows, deleted
    ///     runs paint the old row, added runs the new one.
    /// </summary>
    /// <param name="oldLine">Old line. Null is an empty line.</param>
    /// <param name="newLine">New line. Null is an empty line.</param>
    /// <param name="sides">The projected sides; empty on degradation.</param>
    /// <returns>True when the pilot segmented; false when the caller should fall back.</returns>
    public static bool TrySegmentWords(string? oldLine, string? newLine, out WordDiffSides sides)
    {
        sides = new WordDiffSides(Array.Empty<WordSeg>(), Array.Empty<WordSeg>());
        string oldText = oldLine ?? string.Empty;
        string newText = newLine ?? string.Empty;
        if (oldText.Length > WordDiff.MaxPairableLineChars || newText.Length > WordDiff.MaxPairableLineChars)
        {
            return false;
        }

        try
        {
            DiffPlex.Model.DiffResult result = Differ.Instance.CreateDiffs(
                oldText,
                newText,
                ignoreWhiteSpace: false,
                ignoreCase: false,
                WordChunker.Instance);

            IReadOnlyList<string> oldPieces = result.PiecesOld;
            IReadOnlyList<string> newPieces = result.PiecesNew;

            var oldTokens = new List<(WordSegKind Kind, string Text)>();
            var newTokens = new List<(WordSegKind Kind, string Text)>();
            int oldCursor = 0;
            int newCursor = 0;

            for (int b = 0; b < result.DiffBlocks.Count; b++)
            {
                DiffPlex.Model.DiffBlock block = result.DiffBlocks[b];

                // Clamp positions from the engine: a block behind a cursor is
                // skipped and an overrun is cut. A coarser answer, never a crash.
                int deleteStart = Math.Min(Math.Max(block.DeleteStartA, oldCursor), oldPieces.Count);
                int insertStart = Math.Min(Math.Max(block.InsertStartB, newCursor), newPieces.Count);
                int deleteEnd = Math.Min(block.DeleteStartA + block.DeleteCountA, oldPieces.Count);
                int insertEnd = Math.Min(block.InsertStartB + block.InsertCountB, newPieces.Count);

                while (oldCursor < deleteStart && newCursor < insertStart)
                {
                    oldTokens.Add((WordSegKind.Equal, oldPieces[oldCursor++]));
                    newTokens.Add((WordSegKind.Equal, newPieces[newCursor++]));
                }

                while (oldCursor < deleteStart)
                {
                    oldTokens.Add((WordSegKind.Deleted, oldPieces[oldCursor++]));
                }

                while (newCursor < insertStart)
                {
                    newTokens.Add((WordSegKind.Added, newPieces[newCursor++]));
                }

                for (int k = deleteStart; k < deleteEnd; k++)
                {
                    oldTokens.Add((WordSegKind.Deleted, oldPieces[k]));
                }

                for (int k = insertStart; k < insertEnd; k++)
                {
                    newTokens.Add((WordSegKind.Added, newPieces[k]));
                }

                oldCursor = Math.Max(oldCursor, deleteEnd);
                newCursor = Math.Max(newCursor, insertEnd);
            }

            while (oldCursor < oldPieces.Count && newCursor < newPieces.Count)
            {
                oldTokens.Add((WordSegKind.Equal, oldPieces[oldCursor++]));
                newTokens.Add((WordSegKind.Equal, newPieces[newCursor++]));
            }

            while (oldCursor < oldPieces.Count)
            {
                oldTokens.Add((WordSegKind.Deleted, oldPieces[oldCursor++]));
            }

            while (newCursor < newPieces.Count)
            {
                newTokens.Add((WordSegKind.Added, newPieces[newCursor++]));
            }

            sides = new WordDiffSides(Coalesce(oldTokens), Coalesce(newTokens));
            return true;
        }
        catch (Exception)
        {
            // Same pilot invariant as above: never break a preview.
            sides = new WordDiffSides(Array.Empty<WordSeg>(), Array.Empty<WordSeg>());
            return false;
        }
    }

    private static bool InputTooLarge(string? before, string? after) =>
        (before?.Length ?? 0) + (after?.Length ?? 0) > MaxPilotInputChars;

    private static string TruncateToLines(string text, int maxLines)
    {
        int lines = 0;
        int i = 0;
        while (i < text.Length && lines < maxLines)
        {
            int nl = text.IndexOf('\n', i);
            if (nl < 0)
            {
                return text;
            }

            lines++;
            i = nl + 1;
        }

        if (i >= text.Length)
        {
            return text;
        }

        return text.Substring(0, i).TrimEnd() + "\n" + DiffPreview.DiffTruncatedSentinel;
    }

    private static IReadOnlyList<WordSeg> Coalesce(List<(WordSegKind Kind, string Text)> tokens)
    {
        var runs = new List<WordSeg>(tokens.Count);
        for (int i = 0; i < tokens.Count;)
        {
            int j = i + 1;
            while (j < tokens.Count && tokens[j].Kind == tokens[i].Kind)
            {
                j++;
            }

            var parts = new string[j - i];
            for (int k = i; k < j; k++)
            {
                parts[k - i] = tokens[k].Text;
            }

            runs.Add(new WordSeg(tokens[i].Kind, string.Concat(parts)));
            i = j;
        }

        return runs;
    }
}
