using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Pure transcript extractors backing the builtin panels
///     (todo-list, diff-preview, diagnostics). Reads only from
///     <see cref="UiState.Lines" /> so every renderer (Spectre, CellForge, …)
///     shares one parsing implementation. No filesystem, no Spectre, no DI —
///     only <c>Harbor.Ui.Framework.State</c>.
/// </summary>
public static class PanelExtractors
{
    /// <summary>Tool names tracked by <see cref="ExtractRecentChanges" />.</summary>
    private static readonly FrozenSet<string> TrackedTools = FrozenSet.ToFrozenSet(
        new[] { "edit", "write", "read", "patch" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TodoRegex = new(
        @"^\s*\[(?<marker>[ ~xX\?])\]\s*(?<content>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex ToolNameRegex = new(
        @"^→\s*(?<tool>[A-Za-z0-9_\-]+)",
        RegexOptions.Compiled);

    private static readonly Regex PathJsonRegex = new(
        @"""(path|filePath|file|filename)""\s*:\s*""(?<p>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex CSharpRegex = new(@"\b(CS|MSB)\d{4}\b", RegexOptions.Compiled);

    private static readonly Regex RustRegex = new(@"error\[E\d+\]", RegexOptions.Compiled);

    private static readonly Regex PythonRegex = new(@"File\s+""[^""]+"",\s*line\s+\d+", RegexOptions.Compiled);

    private static readonly Regex NodeJsRegex = new(
        @"(\bat\s+.*\.m?jsx?:\d+|\b(Type|Reference|Syntax)Error\s*:)",
        RegexOptions.Compiled);

    private static readonly Regex ExceptionRegex = new(
        @"\b\w*exception\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarningRegex = new(
        @"\bwarning\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    ///     Parse the most recent todo block from the transcript. Scans tail to
    ///     head, collects <c>[ ]</c>/<c>[~]</c>/<c>[x]</c> markers from
    ///     <see cref="ChatRole.ToolResult" /> lines, and stops at the first
    ///     <see cref="ChatRole.Tool" /> line so only the freshest block is
    ///     returned. Other roles are skipped, not terminal.
    /// </summary>
    public static IReadOnlyList<TodoItem> ExtractTodos(IReadOnlyList<ChatLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return Array.Empty<TodoItem>();
        }

        var bodies = new List<string>();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            ChatLine line = lines[i];
            if (line.Role == ChatRole.Tool)
            {
                break;
            }

            if (line.Role != ChatRole.ToolResult)
            {
                continue;
            }

            bodies.Add(StripResultPrefix(line.Text ?? string.Empty));
        }

        var items = new List<TodoItem>();
        for (int b = bodies.Count - 1; b >= 0; b--)
        {
            foreach (string row in bodies[b].Split('\n'))
            {
                Match m = TodoRegex.Match(row);
                if (!m.Success)
                {
                    continue;
                }

                string content = m.Groups["content"].Value.Trim();
                if (content.Length == 0)
                {
                    continue;
                }

                items.Add(new TodoItem("[" + m.Groups["marker"].Value + "]", content));
            }
        }

        return items;
    }

    /// <summary>Overload reading from <see cref="UiState.Lines" />.</summary>
    public static IReadOnlyList<TodoItem> ExtractTodos(UiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExtractTodos(state.Lines);
    }

    /// <summary>
    ///     Pair tracked tool calls (<c>edit</c>/<c>write</c>/<c>read</c>/<c>patch</c>)
    ///     with their results, most-recent-first. The tool name and file path come
    ///     from the <see cref="ChatRole.Tool" /> line
    ///     (<c>→ {tool} {args-json}</c>); <see cref="PanelFileChange.IsError" />
    ///     and <see cref="PanelFileChange.DiffBody" /> come from the
    ///     <see cref="ChatRole.ToolResult" /> <c>✓</c>/<c>✗</c> prefix and body.
    ///     Untracked tools are skipped; a result without a paired tool line is
    ///     reported with <c>&lt;unknown&gt;</c> names.
    /// </summary>
    public static IReadOnlyList<PanelFileChange> ExtractRecentChanges(IReadOnlyList<ChatLine> lines, int maxCount = 8)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0 || maxCount <= 0)
        {
            return Array.Empty<PanelFileChange>();
        }

        var result = new List<PanelFileChange>();
        for (int i = lines.Count - 1; i >= 0 && result.Count < maxCount; i--)
        {
            ChatLine line = lines[i];
            if (line.Role != ChatRole.ToolResult)
            {
                continue;
            }

            string text = line.Text ?? string.Empty;
            bool isError = text.StartsWith("✗", StringComparison.Ordinal);
            string diffBody = StripResultPrefix(text).Trim();

            string toolName = "<unknown>";
            string filePath = "<unknown>";
            int toolIndex = FindToolIndex(lines, i, line.ToolCallId);
            if (toolIndex >= 0)
            {
                string toolText = lines[toolIndex].Text ?? string.Empty;
                toolName = ExtractToolName(toolText);
                if (!TrackedTools.Contains(toolName))
                {
                    continue;
                }

                filePath = ExtractPath(toolText);
            }

            result.Add(new PanelFileChange(toolName, filePath, diffBody, isError));
        }

        return result;
    }

    /// <summary>Overload reading from <see cref="UiState.Lines" />.</summary>
    public static IReadOnlyList<PanelFileChange> ExtractRecentChanges(UiState state, int maxCount = 8)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExtractRecentChanges(state.Lines, maxCount);
    }

    /// <summary>
    ///     Collect diagnostics from <see cref="ChatRole.ToolResult" /> (bash output)
    ///     and <see cref="ChatRole.Error" /> lines in transcript order. At most one
    ///     diagnostic per physical line (first matching detector wins): C# (<c>CS/MSB####</c>),
    ///     Rust (<c>error[E####]</c>), Python (<c>File "…", line N</c>), Node
    ///     (<c>TypeError:</c> / <c>at …js:line</c>), generic <c>*Exception</c>, or
    ///     bare <c>warning</c>. Lines with <see cref="ChatRole.Error" /> that match
    ///     nothing are still reported with source <c>error</c>.
    /// </summary>
    public static IReadOnlyList<PanelDiagnostic> CollectDiagnostics(IReadOnlyList<ChatLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return Array.Empty<PanelDiagnostic>();
        }

        var result = new List<PanelDiagnostic>();
        for (int i = 0; i < lines.Count; i++)
        {
            ChatLine line = lines[i];
            if (line.Role != ChatRole.ToolResult && line.Role != ChatRole.Error)
            {
                continue;
            }

            string text = line.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            string body = line.Role == ChatRole.ToolResult ? StripResultPrefix(text) : text;
            foreach (string raw in body.Split('\n'))
            {
                string row = raw.Trim();
                if (row.Length == 0)
                {
                    continue;
                }

                if (TryClassify(row, out PanelDiagnostic? diagnostic) && diagnostic is not null)
                {
                    result.Add(diagnostic);
                }
                else if (line.Role == ChatRole.Error)
                {
                    result.Add(new PanelDiagnostic(PanelDiagnosticSeverity.Error, "error", row));
                }
            }
        }

        return result;
    }

    /// <summary>Overload reading from <see cref="UiState.Lines" />.</summary>
    public static IReadOnlyList<PanelDiagnostic> CollectDiagnostics(UiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CollectDiagnostics(state.Lines);
    }

    private static int FindToolIndex(IReadOnlyList<ChatLine> lines, int resultIndex, string? toolCallId)
    {
        if (!string.IsNullOrEmpty(toolCallId))
        {
            for (int j = resultIndex - 1; j >= 0; j--)
            {
                if (lines[j].Role == ChatRole.Tool &&
                    string.Equals(lines[j].ToolCallId, toolCallId, StringComparison.Ordinal))
                {
                    return j;
                }
            }
        }

        for (int j = resultIndex - 1; j >= 0; j--)
        {
            if (lines[j].Role == ChatRole.Tool)
            {
                return j;
            }
        }

        return -1;
    }

    private static string ExtractToolName(string toolText)
    {
        if (string.IsNullOrEmpty(toolText))
        {
            return "<unknown>";
        }

        Match m = ToolNameRegex.Match(toolText);
        return m.Success ? m.Groups["tool"].Value.Trim() : "<unknown>";
    }

    private static string ExtractPath(string toolText)
    {
        int brace = toolText.IndexOf('{');
        if (brace >= 0)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(toolText[brace..]);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string[] keys = ["path", "filePath", "file", "filename"];
                    foreach (string key in keys)
                    {
                        if (doc.RootElement.TryGetProperty(key, out JsonElement value) &&
                            value.ValueKind == JsonValueKind.String)
                        {
                            string? s = value.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                return s.Trim();
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the regex fallback below.
            }
        }

        Match m = PathJsonRegex.Match(toolText);
        return m.Success ? m.Groups["p"].Value.Trim() : "<unknown>";
    }

    private static bool TryClassify(string row, out PanelDiagnostic? diagnostic)
    {
        PanelDiagnosticSeverity severity = WarningRegex.IsMatch(row)
            ? PanelDiagnosticSeverity.Warning
            : PanelDiagnosticSeverity.Error;

        if (CSharpRegex.IsMatch(row))
        {
            diagnostic = new PanelDiagnostic(severity, "csharp", row);
            return true;
        }

        if (RustRegex.IsMatch(row))
        {
            diagnostic = new PanelDiagnostic(severity, "rust", row);
            return true;
        }

        if (PythonRegex.IsMatch(row))
        {
            diagnostic = new PanelDiagnostic(severity, "python", row);
            return true;
        }

        if (NodeJsRegex.IsMatch(row))
        {
            diagnostic = new PanelDiagnostic(severity, "node", row);
            return true;
        }

        if (ExceptionRegex.IsMatch(row))
        {
            diagnostic = new PanelDiagnostic(severity, "exception", row);
            return true;
        }

        if (WarningRegex.IsMatch(row))
        {
            diagnostic = new PanelDiagnostic(PanelDiagnosticSeverity.Warning, "warning", row);
            return true;
        }

        diagnostic = null;
        return false;
    }

    private static string StripResultPrefix(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text[0] == '✓' || text[0] == '✗')
        {
            if (text.Length > 1 && text[1] == ' ')
            {
                return text[2..];
            }

            return text[1..];
        }

        return text;
    }
}

/// <summary>One todo row parsed from a <c>todo</c> tool result.</summary>
/// <param name="Marker">Status marker with brackets: <c>[ ]</c>, <c>[~]</c>, <c>[x]</c>.</param>
/// <param name="Content">Todo text after the marker.</param>
public sealed record TodoItem(string Marker, string Content);

/// <summary>One recent file change: a tracked tool call paired with its result.</summary>
/// <param name="ToolName">Tool name as written in the transcript (<c>edit</c>, <c>write</c>, …).</param>
/// <param name="FilePath">File path from the tool args, or <c>&lt;unknown&gt;</c>.</param>
/// <param name="DiffBody">Tool result body without the <c>✓</c>/<c>✗</c> prefix.</param>
/// <param name="IsError">True when the result line carries the <c>✗</c> prefix.</param>
public sealed record PanelFileChange(string ToolName, string FilePath, string DiffBody, bool IsError);

/// <summary>Severity of a collected diagnostic.</summary>
public enum PanelDiagnosticSeverity
{
    Error,
    Warning,
}

/// <summary>One diagnostic: a single physical transcript line classified by detector.</summary>
/// <param name="Severity">Error, or Warning when the line mentions <c>warning</c>.</param>
/// <param name="Source">Detector id: <c>csharp</c>, <c>rust</c>, <c>python</c>, <c>node</c>, <c>exception</c>, <c>warning</c>, <c>error</c>.</param>
/// <param name="Message">Trimmed source line.</param>
public sealed record PanelDiagnostic(PanelDiagnosticSeverity Severity, string Source, string Message);
