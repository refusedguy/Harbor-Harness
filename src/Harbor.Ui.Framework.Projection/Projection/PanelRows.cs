using System.Globalization;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Shared plain-text row builders for the builtin panels
///     (todo-list, token-breakdown, diagnostics, diff-preview). Pure BCL:
///     data in, <see cref="IReadOnlyList{T}" /> of display rows out.
///     Every renderer (CellForge, Spectre, …) consumes the same rows —
///     CellForge paints them as-is, Spectre wraps them in markup.
///     Layouts mirror the original CellForge panels exactly.
/// </summary>
public static class PanelRows
{
    /// <summary>Todo-list rows: header, Spectre-parity icons, done/active/pending summary.</summary>
    public static List<string> TodoRows(IReadOnlyList<TodoItem> todos, int width)
    {
        ArgumentNullException.ThrowIfNull(todos);
        var rows = new List<string>(todos.Count + 4);
        rows.Add($"Todo List ({todos.Count} items)");
        rows.Add(PanelText.Separator);
        if (todos.Count == 0)
        {
            rows.Add("No todos yet.");
            rows.Add("Ask the agent to use the todo tool.");
        }
        else
        {
            int done = 0;
            int active = 0;
            int pending = 0;
            int contentBudget = Math.Max(1, width - 6);
            for (int i = 0; i < todos.Count; i++)
            {
                string icon = todos[i].Marker switch
                {
                    "[x]" or "[X]" => "✓",
                    "[~]" => "→",
                    "[ ]" => "○",
                    _ => "?",
                };
                switch (todos[i].Marker)
                {
                    case "[x]":
                    case "[X]":
                        done++;
                        break;
                    case "[~]":
                        active++;
                        break;
                    case "[ ]":
                        pending++;
                        break;
                }

                rows.Add($"  {icon}  {PanelText.Truncate(todos[i].Content, contentBudget)}");
            }

            rows.Add(PanelText.Separator);
            rows.Add($"✓ {done}  → {active}  ○ {pending}");
        }

        return rows;
    }

    /// <summary>Token-breakdown rows: cumulative totals with █/░ bars.</summary>
    /// <param name="input">Cumulative input tokens.</param>
    /// <param name="output">Cumulative output tokens.</param>
    /// <param name="width">Available columns.</param>
    /// <param name="cost">
    ///     Cumulative cost as the core priced it, or a lower bound when
    ///     <paramref name="isCostUnpriced" /> is <see langword="true" />.
    /// </param>
    /// <param name="isCostUnpriced">
    ///     <see langword="true" /> when the core could NOT price this session —
    ///     the model publishes no rate table, so the number is a floor and not a
    ///     bill. The money cell then reads <see cref="StatusBarText.UnknownCostCell" />
    ///     instead of a fabricated "$0.0000" (#942).
    ///     <para>
    ///         A genuinely free model reports the same way, because
    ///         <c>Pricing.IsUnknown</c> is true for it too — the core cannot tell
    ///         "free" from "unpriced" and deliberately says "unknown" rather than
    ///         risk the false claim. See <c>SessionMetadata.IsCostKnown</c>.
    ///     </para>
    /// </param>
    public static List<string> TokenRows(
        long input,
        long output,
        decimal cost,
        int width,
        bool isCostUnpriced = false)
    {
        long scale = Math.Max(input, Math.Max(output, 1));
        int barWidth = Math.Max(0, width - 24);
        var rows = new List<string>(7);
        rows.Add("Token Breakdown");
        rows.Add(PanelText.Separator);
        rows.Add($"in    {PanelText.FormatCount(input).PadLeft(12)}  {PanelText.Bar(input, barWidth, scale)}".TrimEnd());
        rows.Add($"out   {PanelText.FormatCount(output).PadLeft(12)}  {PanelText.Bar(output, barWidth, scale)}".TrimEnd());
        rows.Add(PanelText.Separator);
        // #682: the money cell's SHAPE is StatusBarText.CostToUsd, not a fifth
        // hand-rolled "$" + …"F4" in a panel row. Byte-identical output; one
        // writer.
        //
        // #942: and the core's own "I could not price this" bit has to REACH
        // that writer. This row used to call CostToUsd(cost) unconditionally, so
        // an unpriced session — 11 of the 13 shipped providers, every one of
        // them served by a catalogue entry with no rates — printed "$0.0000"
        // here while the status bar beside it printed "—". Same session, same
        // frame, two different answers.
        //
        // The glyph is UnknownCostCell, NOT CostCell: CostCell hides the money
        // cell at zero (the #457 "no data ⇒ no cell" placement rule for the
        // projected bar), and this panel is positional — the total row is part
        // of the table and cannot drop a column. That is the same
        // placement-vs-shape split TuiViewModels.CostText documents.
        string money = isCostUnpriced
            ? StatusBarText.UnknownCostCell
            : StatusBarText.CostToUsd(cost);
        rows.Add($"total {PanelText.FormatCount(input + output).PadLeft(12)}  {money}");
        rows.Add("(cumulative session totals)");
        return rows;
    }

    /// <summary>
    ///     Diagnostics rows: cursor window over the core's classified issues,
    ///     j/k navigation. Both producers are drawn — a language server's own
    ///     report and issues detected in a tool's output — and
    ///     <see cref="PanelDiagnostic.Origin" /> is what told them apart, not a
    ///     pattern run here (#674).
    /// </summary>
    public static List<string> DiagnosticsRows(IReadOnlyList<PanelDiagnostic> diagnostics, int cursor, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var rows = new List<string>(diagnostics.Count + 4);
        rows.Add($"Diagnostics ({diagnostics.Count} issue(s))");
        rows.Add(PanelText.Separator);
        if (diagnostics.Count == 0)
        {
            rows.Add("No diagnostics reported.");
            rows.Add("Language-server reports and detected tool output land here.");
        }
        else
        {
            int selected = Math.Clamp(cursor, 0, diagnostics.Count - 1);
            int maxVisible = Math.Max(2, height - 4);
            int start = Math.Max(0, selected - maxVisible + 1);
            int end = Math.Min(diagnostics.Count, start + maxVisible);
            int messageBudget = Math.Max(1, width - 4);
            for (int i = start; i < end; i++)
            {
                var diagnostic = diagnostics[i];
                string marker = i == selected ? ">" : " ";
                string icon = diagnostic.Severity == PanelDiagnosticSeverity.Warning ? "▲" : "✗";
                rows.Add($"{marker} {icon} {PanelText.Truncate(diagnostic.Message, messageBudget)}");
            }

            rows.Add(PanelText.Separator);
            rows.Add("j/k move");
        }

        return rows;
    }

    /// <summary>Diff-preview rows: header per change plus up to 4 body lines.</summary>
    public static List<string> DiffRows(IReadOnlyList<PanelFileChange> changes, int width)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var rows = new List<string>(changes.Count * 5 + 4);
        rows.Add($"Diff Preview ({changes.Count} recent change(s))");
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
                var change = changes[i];
                // The tool's own glyph, carried on the record (#680). This used to be a
                // four-arm table keyed by tool name — the fourth such table in the
                // UI layer, and the three chat-card ones already disagreed.
                string icon = change.Glyph.Length > 0 ? change.Glyph : "·";
                string ok = change.IsError ? "✗" : "✓";
                string path = ShortenPath(change.FilePath, Math.Max(4, width - 12));
                rows.Add($"{icon} {ok} {path}");
                if (!string.IsNullOrEmpty(change.DiffBody))
                {
                    string body = change.DiffBody;
                    int start = 0;
                    int bodyBudget = Math.Max(1, width - 2);
                    for (int shown = 0; shown < 4 && start < body.Length; shown++)
                    {
                        int nl = body.IndexOf('\n', start);
                        string line = nl < 0 ? body[start..] : body[start..nl];
                        start = nl < 0 ? body.Length : nl + 1;
                        rows.Add("  " + PanelText.Truncate(line.TrimEnd('\r'), bodyBudget));
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>Session sidebar rows: active session highlighted, loading footer.</summary>
    public static List<string> SessionRows(IReadOnlyList<SessionInfo> sessions, SessionId? activeSessionId, bool isLoading, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var rows = new List<string>(sessions.Count + 4);
        rows.Add("Sessions");
        rows.Add(PanelText.Separator);
        if (sessions.Count == 0)
        {
            rows.Add("(no sessions)");
            rows.Add("Start a new session to see it here.");
        }
        else
        {
            int maxVisible = Math.Max(2, height - 4);
            int end = Math.Min(sessions.Count, maxVisible);
            for (int i = 0; i < end; i++)
            {
                var session = sessions[i];
                bool isActive = activeSessionId is not null && activeSessionId.Equals(session.SessionId);
                string marker = isActive ? "▸" : " ";
                rows.Add($"{marker} {PanelText.Truncate(session.Title, Math.Max(1, width - 3))}");
            }

            if (sessions.Count > maxVisible)
            {
                rows.Add($"  ↓ {sessions.Count - maxVisible} more below");
            }
        }

        rows.Add(PanelText.Separator);
        rows.Add(isLoading ? "loading…" : $"{sessions.Count} session(s)");
        return rows;
    }

    /// <summary>Log panel rows: level tags, timestamps, truncated bodies.</summary>
    public static List<string> LogRows(IReadOnlyList<DiagnosticEntry> entries, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = new List<string>(16);
        rows.Add("Logs (F12 to hide · live ILogger output · file at ~/.harbor/logs/)");
        rows.Add(PanelText.Separator);
        if (entries.Count == 0)
        {
            rows.Add("No log entries yet.");
            rows.Add("Logs from every ILogger will appear here in arrival order.");
            return rows;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // #563: the mnemonic comes from the one table that spells it. This
            // used to be a local switch ending in a 4-question-mark sentinel,
            // while the two FileLogger copies answered the same question with
            // `level.ToString().ToUpperInvariant()` — so one event rendered as
            // that sentinel here and as "VERBOSE" in the log file.
            string levelTag = LogLevelTag.For(entry.Level);
            string category = ShortenCategory(entry.Category);
            int budget = width - LogRowFormat.TimestampWidth - LogLevelTag.Width - category.Length - 7;
            string body = PanelText.SingleLine(entry.Message);
            if (budget <= 0)
            {
                body = string.Empty;
            }
            else if (body.Length > budget)
            {
                body = budget == 1 ? "…" : body[..(budget - 1)] + "…";
            }

            // #563: the row layout belongs to LogRowFormat, not to this loop.
            // It used to be an interpolation here, and a consumer elsewhere
            // re-read the level out of the finished string at a hard-coded
            // [13..17] — an offset that only ever described THIS producer.
            rows.Add(new LogRow(entry.Timestamp, entry.Level, category, body).Format());
        }

        rows.Add(PanelText.Separator);
        rows.Add("F12 toggle · Ctrl+L clear console (does not clear this buffer)");
        return rows;
    }

    /// <summary>One file-tree entry for row building (navigation state stays in the panel).</summary>
    public sealed record FileTreeRow(string Name, bool IsDirectory, bool IsHidden);

    /// <summary>
    ///     File-tree rows for a loaded listing: cursor window with over/underflow
    ///     markers.
    /// </summary>
    /// <param name="displayDir">The directory whose contents are shown.</param>
    /// <param name="entries">The entries, in display order.</param>
    /// <param name="cursor">Zero-based cursor, clamped by the caller.</param>
    /// <param name="width">Available columns.</param>
    /// <param name="height">Available rows.</param>
    public static List<string> FileTreeRows(
        string displayDir,
        IReadOnlyList<FileTreeRow> entries,
        int cursor,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return FileTreeBody(entries, cursor, width, height, displayDir, FileTreeFooter.Empty);
    }

    /// <summary>
    ///     File-tree rows for a snapshot that is not necessarily loaded (#667).
    ///     Renders the same loaded view as the entry-point overload, and adds the
    ///     two states a lazy listing makes possible: a load in flight, and a load
    ///     that failed.
    /// </summary>
    /// <remarks>
    ///     The status is passed rather than read from a provider field on purpose:
    ///     the whole point of #667 is that the view can no longer tell "empty
    ///     because the directory is empty" from "empty because nothing has loaded
    ///     yet" by looking at its own state, and the one thing that CAN tell them
    ///     apart is the status that arrived with the entries.
    /// </remarks>
    /// <param name="displayDir">The directory whose contents are shown.</param>
    /// <param name="snapshot">The listing and its load status.</param>
    /// <param name="rows">The projected entry rows, in display order.</param>
    /// <param name="cursor">Zero-based cursor, clamped by the caller.</param>
    /// <param name="width">Available columns.</param>
    /// <param name="height">Available rows.</param>
    public static List<string> FileTreeRows(
        string displayDir,
        FileTreeSnapshot snapshot,
        IReadOnlyList<FileTreeRow> rows,
        int cursor,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rows);

        if (snapshot.Status == AsyncStatus.Error)
        {
            return FileTreePlaceholder(
                displayDir,
                width,
                "(cannot read)",
                snapshot.Error,
                isError: true);
        }

        if (rows.Count == 0 && snapshot.IsPending)
        {
            return FileTreePlaceholder(displayDir, width, "(loading…)", reason: null, isError: false);
        }

        var footer = snapshot.Truncated
            ? FileTreeFooter.Truncated(snapshot.TotalCount)
            : FileTreeFooter.Empty;

        return FileTreeBody(rows, cursor, width, height, displayDir, footer);
    }

    /// <summary>Shared body: header, path, separator, the cursor window, the footer.</summary>
    private static List<string> FileTreeBody(
        IReadOnlyList<FileTreeRow> entries,
        int cursor,
        int width,
        int height,
        string displayDir,
        string footerNote)
    {
        var rows = new List<string>(entries.Count + 7);
        rows.Add("File Tree");
        rows.Add(PanelText.ShortenTail(displayDir, Math.Max(0, width - 2)));
        rows.Add(PanelText.Separator);
        if (entries.Count == 0)
        {
            rows.Add("(empty directory)");
            return rows;
        }

        int maxVisible = Math.Max(2, height - 4);
        int start = Math.Max(0, cursor - maxVisible + 1);
        int end = Math.Min(entries.Count, start + maxVisible);
        if (start > 0)
        {
            rows.Add("  ↑ more above");
        }

        int nameBudget = Math.Max(1, width - 6);
        for (int i = start; i < end; i++)
        {
            var entry = entries[i];
            string marker = i == cursor ? ">" : " ";
            string icon = entry.IsDirectory ? "▸" : entry.IsHidden ? "·" : " ";
            rows.Add($"{marker} {icon} {PanelText.Truncate(entry.Name, nameBudget)}");
        }

        if (end < entries.Count)
        {
            rows.Add("  ↓ more below");
        }

        rows.Add(PanelText.Separator);
        rows.Add("j/k move · Enter open · h parent · r refresh");
        if (!string.IsNullOrEmpty(footerNote))
        {
            rows.Add(footerNote);
        }

        return rows;
    }

    /// <summary>
    ///     The two states that are not a listing: a walk in flight, and a walk
    ///     that failed. Both keep the header and the path, so the panel does not
    ///     blink out of existence while the filesystem is being asked.
    /// </summary>
    private static List<string> FileTreePlaceholder(
        string displayDir,
        int width,
        string label,
        string? reason,
        bool isError)
    {
        var rows = new List<string>(5)
        {
            "File Tree",
            PanelText.ShortenTail(displayDir, Math.Max(0, width - 2)),
            PanelText.Separator,
            label,
        };

        if (!string.IsNullOrEmpty(reason))
        {
            // The reason is whatever the loader phrased — a filesystem message,
            // or an exception's. It is user-visible text of unknown length, so it
            // is clipped like any other row rather than trusted to fit.
            rows.Add(PanelText.Truncate(reason, Math.Max(1, width - 2)));
        }

        if (isError)
        {
            rows.Add("r retry");
        }

        return rows;
    }

    /// <summary>
    ///     The one line that can follow the key hints. A truncated listing says
    ///     so, because a silently shortened tree is indistinguishable from a
    ///     complete one and that is a lie the user cannot detect.
    /// </summary>
    private static class FileTreeFooter
    {
        public static string Empty { get; } = string.Empty;

        public static string Truncated(int totalCount) => totalCount > 0
            ? $"… showing the first entries of at least {totalCount} · r reloads nothing more"
            : "… listing truncated · this directory has more entries than the cap";
    }

    /// <summary>Skill-freshness rows: header, one pill row per skill, stale summary.</summary>
    public static List<string> SkillFreshnessRows(IReadOnlyList<SkillFreshnessEntry> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var rows = new List<string>(skills.Count + 5);
        rows.Add($"Skills ({skills.Count})");
        rows.Add(PanelText.Separator);
        if (skills.Count == 0)
        {
            rows.Add("No skills installed.");
            rows.Add("Drop a SKILL.md in .harbor/skills/ to add one.");
        }
        else
        {
            int stale = 0;
            for (int i = 0; i < skills.Count; i++)
            {
                if (skills[i].IsStale)
                {
                    stale++;
                }

                rows.Add($"{skills[i].PillText}  {skills[i].Name}");
            }

            rows.Add(PanelText.Separator);
            rows.Add(stale == 0 ? "All skills up to date." : $"{stale} need attention (sync skills-lock.json).");
        }

        return rows;
    }

    private static string ShortenPath(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
        {
            return path;
        }

        // Keep the file name + a hint of the directory (mirrors the Spectre DiffPreviewPanel).
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (slash < 0 || path.Length - slash > max - 3)
        {
            return path[^(max - 1)] + "…" + path[^1];
        }

        string file = path[slash..];
        string dir = path[..slash];

        // #482: `budget` is the width left for the directory hint and may legitimately
        // reach 0 (a long file name eats the whole row), which drives the slice length
        // to -1. `dir[^(-1)..]` builds an Index from a negative value and throws
        // ArgumentOutOfRangeException, so clamp the length. 0 stays 0: `^0` is
        // Index.End (empty suffix), i.e. the bare "…" form.
        int budget = max - file.Length - 3;
        if (dir.Length > budget)
        {
            int keep = Math.Max(0, budget - 1);
            dir = keep == 0 ? "…" : "…" + dir[^keep..];
        }

        return dir + file;
    }

    private static string ShortenCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return "-";
        }

        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }
}

/// <summary>
///     Shared plain-text row helpers: geometry clipping plus truncation
///     utilities. Moved up from the CellForge-native panels so every
///     renderer shares one implementation.
/// </summary>
public static class PanelText
{
    /// <summary>Plain separator stamped between panel sections.</summary>
    public const string Separator = "────────────────────────";

    /// <summary>
    ///     Clip rows to the available geometry: at most <paramref name="height" />
    ///     rows, each at most <paramref name="width" /> columns (hard-truncated with
    ///     <c>…</c>). Returns an empty list for non-positive geometry instead of
    ///     throwing, so tiny viewports degrade gracefully.
    /// </summary>
    public static IReadOnlyList<string> Clip(List<string> rows, int width, int height)
    {
        if (rows.Count == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<string>();
        }

        int take = Math.Min(rows.Count, height);
        var clipped = new List<string>(take);
        for (int i = 0; i < take; i++)
        {
            string line = rows[i];
            clipped.Add(line.Length <= width ? line : Truncate(line, width));
        }

        return clipped;
    }

    /// <summary>Hard-truncate <paramref name="text" /> to <paramref name="max" /> columns.</summary>
    public static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        return max == 1 ? "…" : text[..(max - 1)] + "…";
    }

    /// <summary>Keep the tail of a path visible (filename first), prefix with <c>…</c>.</summary>
    public static string ShortenTail(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || max <= 0)
        {
            return max <= 0 ? string.Empty : path;
        }

        if (path.Length <= max)
        {
            return path;
        }

        return max == 1 ? "…" : "…" + path[^(max - 1)..];
    }

    /// <summary>Collapse a multi-line log message onto one display row.</summary>
    /// <remarks>
    ///     This <b>substitutes</b> CR and LF with a space; it does not delete
    ///     whitespace. Length is preserved, tabs and space runs survive, and the
    ///     result can still be budgeted by column count.
    ///     <para>
    ///         The E2E tear-tolerant matcher
    ///         (<c>TuiDriver.StripWhitespace</c> in tests/Harbor.E2E.Framework)
    ///         used to be called <c>CollapseWhitespace</c> and does the opposite:
    ///         it <b>deletes</b> every whitespace character so a pattern still
    ///         matches after a renderer tore a row across grid lines. It was
    ///         renamed when #574's duplicate census surfaced the name collision,
    ///         and the two behaviours are pinned apart by
    ///         <c>WhitespaceCollapseDivergenceTests</c>. Do not fold one into
    ///         the other — they are separate requirements, not one helper
    ///         written twice.
    ///     </para>
    /// </remarks>
    public static string SingleLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    /// <summary>Compact count formatting (123 → "123", 1500 → "1.5K", 2M → "2.00M").</summary>
    public static string FormatCount(long n) =>
        n >= 1_000_000
            ? (n / 1_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + "M"
            : n >= 1_000
                ? (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K"
                : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Proportional █/░ bar of <paramref name="width" /> columns.</summary>
    public static string Bar(long value, int width, long scale)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        int filled = (int)((double)value / scale * width);
        if (filled > width)
        {
            filled = width;
        }

        if (filled < 0)
        {
            filled = 0;
        }

        return new string('█', filled) + new string('░', width - filled);
    }
}
