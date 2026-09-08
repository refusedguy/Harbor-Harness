using System.Globalization;

namespace Harbor.Ui.Framework.Projection;

// TODO(principles)[DIP]: Spectre builtins (contrib/.../Panels/Builtin/) still
// carry their own row builders — adopt these shared builders there (wrap rows
// in TextLine.FromMarkup) once contrib has its own verification lane.

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
    /// <summary>Todo-list rows: header, items, done/active/pending summary.</summary>
    public static List<string> TodoRows(IReadOnlyList<TodoItem> todos)
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
            for (int i = 0; i < todos.Count; i++)
            {
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

                rows.Add($"{todos[i].Marker} {todos[i].Content}");
            }

            rows.Add(PanelText.Separator);
            rows.Add($"Done {done} · active {active} · pending {pending}");
        }

        return rows;
    }

    /// <summary>Token-breakdown rows: cumulative totals with █/░ bars.</summary>
    public static List<string> TokenRows(long input, long output, decimal cost, int width)
    {
        long scale = Math.Max(input, Math.Max(output, 1));
        int barWidth = Math.Max(0, width - 24);
        var rows = new List<string>(7);
        rows.Add("Token Breakdown");
        rows.Add(PanelText.Separator);
        rows.Add($"in    {PanelText.FormatCount(input).PadLeft(12)}  {PanelText.Bar(input, barWidth, scale)}".TrimEnd());
        rows.Add($"out   {PanelText.FormatCount(output).PadLeft(12)}  {PanelText.Bar(output, barWidth, scale)}".TrimEnd());
        rows.Add(PanelText.Separator);
        rows.Add($"total {PanelText.FormatCount(input + output).PadLeft(12)}  ${cost.ToString("F4", CultureInfo.InvariantCulture)}");
        rows.Add("(cumulative session totals)");
        return rows;
    }

    /// <summary>Diagnostics rows: one row per issue (read-only window).</summary>
    public static List<string> DiagnosticsRows(IReadOnlyList<PanelDiagnostic> diagnostics, int height)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var rows = new List<string>(diagnostics.Count + 4);
        rows.Add($"Diagnostics ({diagnostics.Count} issue(s))");
        rows.Add(PanelText.Separator);
        if (diagnostics.Count == 0)
        {
            rows.Add("No diagnostics detected.");
            rows.Add("Errors emitted by the `bash` tool will show up here.");
        }
        else
        {
            int maxVisible = Math.Max(2, height - 4);
            int end = Math.Min(diagnostics.Count, maxVisible);
            for (int i = 0; i < end; i++)
            {
                var diagnostic = diagnostics[i];
                string icon = diagnostic.Severity == PanelDiagnosticSeverity.Warning ? "▲" : "✗";
                rows.Add($"{icon} {diagnostic.Message}");
            }

            rows.Add(PanelText.Separator);
            rows.Add("read-only · cursor navigation lands in a follow-up");
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
                string icon = change.ToolName switch
                {
                    "edit" => "✎",
                    "write" => "✚",
                    "read" => "▸",
                    "patch" => "⌥",
                    _ => "·",
                };
                string ok = change.IsError ? "✗" : "✓";
                string path = PanelText.ShortenTail(change.FilePath, Math.Max(4, width - 12));
                rows.Add($"{icon} {ok} {path}");
                if (!string.IsNullOrEmpty(change.DiffBody))
                {
                    string body = change.DiffBody;
                    int start = 0;
                    for (int shown = 0; shown < 4 && start < body.Length; shown++)
                    {
                        int nl = body.IndexOf('\n', start);
                        string line = nl < 0 ? body[start..] : body[start..nl];
                        start = nl < 0 ? body.Length : nl + 1;
                        rows.Add("  " + line.TrimEnd('\r'));
                    }
                }
            }
        }

        return rows;
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
