using Harbor.Tui.CellForge.Rendering;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native diff-preview panel: recent <c>edit</c> / <c>write</c> /
///     <c>read</c> / <c>patch</c> tool calls paired with their results via
///     <see cref="PanelExtractors.ExtractRecentChanges(UiState, int)"/>. One header
///     row per change (<c>tool-icon ok-icon path</c>) plus up to 4 diff body lines.
///     Non-interactive.
/// </summary>
/// <remarks>
///     [steal/opencode] O5 hunk-virtualized diff (epic #1155, issue #1174 — port
///     of <c>util/diff.ts splitPatchHunks</c> + <c>patch-diff.tsx</c>): each
///     <c>DiffBody</c> runs through <see cref="HunkVirtualizer.SplitPatchHunks"/>.
///     Added-only runs longer than <see cref="HunkVirtualizer.VirtualChunkLines"/>
///     arrive pre-cut into 128-line chunks with their own <c>@@</c> headers;
///     the gutter is sized from the full-file <c>MaxNewLine</c> so chunk gutters
///     never jitter; bodies at or above
///     <see cref="HunkVirtualizer.VirtualMinLines"/> render a head window plus a
///     virtualization marker; split/unified is picked automatically by
///     <see cref="HunkVirtualizer.PreferSplit"/> (width &gt; 120 → split).
///     The text-row surface stays unified in both modes — the mode is exposed
///     in the title for width-responsive consumers; a true side-by-side cell
///     grid is future work.
/// </remarks>
public sealed class CellForgeDiffPreviewPanel : CellForgePanelBase
{
    /// <summary>Body rows rendered per change before the virtualization marker.</summary>
    internal const int MaxBodyRows = 4;

    /// <inheritdoc />
    public override string Id => "diff-preview";

    /// <inheritdoc />
    public override string Title => "Diff Preview";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public override int DefaultSize => 12;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        Ensure.NotNull(ctx);
        var changes = PanelExtractors.ExtractRecentChanges(ctx.State, 8);
        bool split = HunkVirtualizer.PreferSplit(ctx.Width);
        var rows = new List<string>(changes.Count * (MaxBodyRows + 2) + 4);
        rows.Add($"Diff Preview ({changes.Count} recent change(s), {(split ? "split" : "unified")})");
        rows.Add(PanelText.Separator);
        if (changes.Count == 0)
        {
            rows.Add("No file edits yet.");
            rows.Add("Edits made by the agent will appear here.");
        }
        else
        {
            for (int i = 0; i < changes.Count; i++)
            {
                AppendChange(rows, changes[i], ctx.Width);
            }
        }

        return PanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    private static void AppendChange(List<string> rows, PanelFileChange change, int width)
    {
        string icon = change.Glyph.Length > 0 ? change.Glyph : "·";
        string ok = change.IsError ? "✗" : "✓";
        string path = ShortenPath(change.FilePath, Math.Max(4, width - 12));
        rows.Add($"{icon} {ok} {path}");
        if (string.IsNullOrEmpty(change.DiffBody))
        {
            return;
        }

        VirtualPatch patch = HunkVirtualizer.SplitPatchHunks(change.DiffBody);
        int gutter = HunkVirtualizer.GutterWidth(patch.MaxNewLine);
        int bodyBudget = Math.Max(1, width - 2);
        int shown = 0;
        int totalBodyRows = 0;
        foreach (VirtualHunk hunk in patch.Hunks)
        {
            totalBodyRows += hunk.Lines.Count + (hunk.Header.Length > 0 ? 1 : 0);
        }

        foreach (VirtualHunk hunk in patch.Hunks)
        {
            if (shown >= MaxBodyRows)
            {
                break;
            }

            if (hunk.Header.Length > 0)
            {
                if (shown >= MaxBodyRows)
                {
                    break;
                }

                rows.Add("  " + PanelText.Truncate(hunk.Header.TrimEnd('\r'), bodyBudget));
                shown++;
            }

            int old = hunk.OldStart;
            int nw = hunk.NewStart;
            foreach (string raw in hunk.Lines)
            {
                if (shown >= MaxBodyRows)
                {
                    break;
                }

                string gutterText;
                if (raw.Length > 0 && raw[0] == '-')
                {
                    gutterText = new string(' ', gutter);
                    old++;
                }
                else
                {
                    gutterText = nw.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(gutter);
                    nw++;
                    if (raw.Length == 0 || raw[0] != '+')
                    {
                        old++;
                    }
                }

                string line = gutterText + " " + raw.TrimEnd('\r');
                rows.Add("  " + PanelText.Truncate(line, bodyBudget));
                shown++;
            }
        }

        bool virtualized = HunkVirtualizer.NeedsVirtualization(patch.TotalLines);
        int remaining = totalBodyRows - shown;
        if (virtualized || remaining > 0)
        {
            string marker = virtualized
                ? $"⋯ virtualized: {patch.TotalLines} lines, showing {shown} ({(patch.WasChunked ? "chunked" : "head window")})"
                : $"⋯ {remaining} more line(s)";
            rows.Add("  " + PanelText.Truncate(marker, bodyBudget));
        }
    }

    private static string ShortenPath(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
        {
            return path;
        }

        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (slash < 0 || path.Length - slash > max - 3)
        {
            return path[^(max - 1)] + "…" + path[^1];
        }

        string file = path[slash..];
        string dir = path[..slash];
        int budget = max - file.Length - 3;
        if (dir.Length > budget)
        {
            int keep = Math.Max(0, budget - 1);
            dir = keep == 0 ? "…" : "…" + dir[^keep..];
        }

        return dir + file;
    }
}
