using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Pure transcript extractors backing the builtin panels
///     (todo-list, diff-preview, diagnostics). Reads only from
///     <see cref="UiState" /> so every renderer (Spectre, CellForge, …) shares
///     one implementation. No filesystem, no Spectre, no DI — only
///     <c>Harbor.Ui.Framework.State</c>.
/// </summary>
/// <remarks>
///     The todo and diff extractors read the transcript and parse it, which is
///     presentation of what the agent said. The diagnostics extractor no longer
///     does: it renders rows the headless core already classified (#674).
/// </remarks>
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

    /// <summary>JSON property names probed (in order) for a file path.</summary>
    private static readonly string[] PathKeys = ["path", "filePath", "file", "filename"];

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

    /// <summary>Overload reading from <see cref="UiState.Chat.Lines" />.</summary>
    public static IReadOnlyList<TodoItem> ExtractTodos(UiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExtractTodos(state.Chat.Lines);
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

    /// <summary>
    ///     Overload reading the transcript AND the structured tool calls
    ///     (#680). This is the one that can fill <see cref="PanelFileChange.Glyph" />:
    ///     the published snapshot carries the calling tool's own glyph, so the
    ///     diff panel draws what the tool declared instead of consulting a table of
    ///     its own.
    /// </summary>
    public static IReadOnlyList<PanelFileChange> ExtractRecentChanges(UiState state, int maxCount = 8)
    {
        ArgumentNullException.ThrowIfNull(state);

        IReadOnlyList<PanelFileChange> changes = ExtractRecentChanges(state.Chat.Lines, maxCount);
        if (changes.Count == 0 || state.Chat.ToolCalls.IsEmpty)
        {
            return changes;
        }

        // Keyed by file path: the panel shows the FILE, and two calls may touch
        // the same one.
        var glyphByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ToolCallSnapshot call in state.Chat.ToolCalls)
        {
            if (call.IsDiffTool
                && call.DiffFilePath is { Length: > 0 } path
                && !glyphByPath.ContainsKey(path))
            {
                glyphByPath[path] = call.Glyph;
            }
        }

        return
        [
            .. changes.Select(change => glyphByPath.TryGetValue(change.FilePath, out string? glyph)
                ? change with { Glyph = glyph }
                : change)
        ];
    }

    /// <summary>
    ///     The diagnostics panel's rows, read straight out of
    ///     <see cref="UiState.Chat.Diagnostics" /> — the snapshot the headless core
    ///     composed and the host pushed. Order is the core's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This used to read <see cref="ChatRole.ToolResult" /> lines — the
    ///         <c>bash</c> tool's OUTPUT — and decide for itself which of them
    ///         were errors, using six detector regexes of its own. Deciding that
    ///         is counting, and counting belongs to the core: the table now lives
    ///         in <c>Harbor.Application.Diagnostics.ToolOutputIssueDetector</c>
    ///         and an architecture guard fails the build if a second copy of it
    ///         appears under <c>src/</c> or <c>apps/</c>.
    ///     </para>
    ///     <para>
    ///         A projection that re-derived severity was worse than merely
    ///         duplicated: the panel it fed was the one that should have been
    ///         showing language-server diagnostics, so «a regex matched a build
    ///         log» stood in for «a language server reported an error» and the
    ///         real channel was never connected. Both producers now arrive here
    ///         pre-classified, and <see cref="PanelDiagnostic.Origin" /> says which
    ///         is which.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<PanelDiagnostic> CollectDiagnostics(UiState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        ImmutableArray<DiagnosticIssue> issues = state.Chat.Diagnostics;
        if (issues.Length == 0)
        {
            return Array.Empty<PanelDiagnostic>();
        }

        var result = new List<PanelDiagnostic>(issues.Length);
        for (int i = 0; i < issues.Length; i++)
        {
            DiagnosticIssue issue = issues[i];
            result.Add(new PanelDiagnostic(
                SeverityOf(issue.Severity),
                issue.Producer,
                Describe(issue),
                issue.Source));
        }

        return result;
    }

    /// <summary>
    ///     The panel's two-icon vocabulary. Only two glyphs exist, so
    ///     <see cref="DiagnosticIssueSeverity.Information" /> and
    ///     <see cref="DiagnosticIssueSeverity.Hint" /> share the warning one: an
    ///     advisory the panel cannot show is better drawn quietly than drawn as
    ///     a failure. The distinction is not lost — it is still in the snapshot.
    /// </summary>
    private static PanelDiagnosticSeverity SeverityOf(DiagnosticIssueSeverity severity) => severity switch
    {
        DiagnosticIssueSeverity.Error => PanelDiagnosticSeverity.Error,
        DiagnosticIssueSeverity.Warning => PanelDiagnosticSeverity.Warning,
        DiagnosticIssueSeverity.Information => PanelDiagnosticSeverity.Warning,
        DiagnosticIssueSeverity.Hint => PanelDiagnosticSeverity.Warning,
        _ => PanelDiagnosticSeverity.Warning,
    };

    /// <summary>
    ///     One row's text: the producer's message, prefixed with
    ///     <c>file:line</c> when the producer named a place. A language server
    ///     always does; a build log usually does not, and the prefix is simply
    ///     absent there rather than shown blank.
    /// </summary>
    private static string Describe(DiagnosticIssue issue)
    {
        if (string.IsNullOrEmpty(issue.FilePath))
        {
            return issue.Message;
        }

        return issue.Line > 0
            ? $"{issue.FilePath}:{issue.Line} {issue.Message}"
            : $"{issue.FilePath} {issue.Message}";
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
                // Slice the string's memory instead of Substring-copying it:
                // JsonDocument parses directly from the shared buffer, so no
                // per-row intermediate string is allocated on this hot path.
                using JsonDocument doc = JsonDocument.Parse(toolText.AsMemory(brace));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (string key in PathKeys)
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
/// <param name="Glyph">
///     The tool's own glyph, carried rather than looked up (#680). A row renderer
///     that wants an icon reads this field; the alternative was a fourth
///     tool-name-keyed glyph table, and the three that already existed disagreed
///     with each other.
/// </param>
public sealed record PanelFileChange(
    string ToolName,
    string FilePath,
    string DiffBody,
    bool IsError,
    string Glyph = "");

/// <summary>Severity of a collected diagnostic.</summary>
public enum PanelDiagnosticSeverity
{
    Error,
    Warning,
}

/// <summary>
///     One issue as the headless core classified it, narrowed to what this panel
///     draws. This is a VIEW row: it is produced from a
///     <see cref="DiagnosticIssue" /> that is already classified, and it decides
///     nothing — deciding is the core's job (#674).
/// </summary>
/// <param name="Severity">Which of the panel's two icons to draw.</param>
/// <param name="Source">Who reported it: a language-server id, or a detector name (<c>csharp</c>, <c>node</c>, …).</param>
/// <param name="Message">Display text, already formatted by the core.</param>
/// <param name="Origin">
///     Which producer this came from. Carried rather than inferred, because a
///     renderer that guesses between «a language server said so» and «a regex
///     matched a build log» is how the two ended up in one panel to begin with.
/// </param>
public sealed record PanelDiagnostic(
    PanelDiagnosticSeverity Severity,
    string Source,
    string Message,
    DiagnosticIssueSource Origin);
