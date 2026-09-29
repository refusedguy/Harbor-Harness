using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     One running (or finished) sub-agent row in the <c>subagents</c> panel:
///     everything a row renders — session identity, human title, agent name, a
///     short status string and the session age — plus the preformatted
///     <see cref="RowText" /> so every renderer paints identical rows.
/// </summary>
/// <param name="SessionId">Stable session id; Enter opens its read-only transcript.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="Agent">Agent name bound to the sub-agent session.</param>
/// <param name="Status">Short status: <c>"running"</c>, <c>"done"</c>, <c>"error"</c>, …</param>
/// <param name="Age">Compact age since last activity (<c>"12s"</c>, <c>"5m"</c>, <c>"3h"</c>, <c>"2d"</c>).</param>
/// <param name="ParentSessionId">Parent session id (branch/fork lineage), or null.</param>
public sealed record SubagentRow(
    string SessionId,
    string Title,
    string Agent,
    string Status,
    string Age,
    string? ParentSessionId)
{
    /// <summary>Single-line row text: title, agent, status, age.</summary>
    public string RowText => $"{Title}  {Agent}  {Status}  {Age}";
}

/// <summary>
///     Pure sub-agents panel model (opencode Subagents-overlay equivalent):
///     selects which sessions become rows and formats the read-only transcript.
///     BCL + Abstractions only, zero rendering — the CellForge
///     <c>CellForgeSubagentsPanel</c> drives it and paints from the results.
///     Row selection mirrors the jump-palette seeder idiom: children of the
///     active session (by <c>ParentSessionId</c>, any kind) plus every
///     sub-agent-kind session, in the store's recency order.
/// </summary>
public static class SubagentsModel
{
    /// <summary>
    ///     Select panel rows from a store snapshot: children of
    ///     <paramref name="activeSessionId" /> plus all sub-agent sessions.
    ///     Input order (the store's recency order) is preserved.
    /// </summary>
    /// <param name="all">Full session snapshot (e.g. <c>ISessionStore.ListAsync</c>).</param>
    /// <param name="activeSessionId">Active session id, or null when unknown.</param>
    /// <param name="now">Reference instant for age computation (injectable for tests).</param>
    public static IReadOnlyList<SubagentRow> BuildRows(
        IReadOnlyList<Session> all,
        string? activeSessionId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(all);
        var rows = new List<SubagentRow>();
        for (int i = 0; i < all.Count; i++)
        {
            var session = all[i];
            bool isChild = activeSessionId is not null
                && session.ParentSessionId == activeSessionId;
            if (!isChild && !session.IsSubagent())
                continue;
            rows.Add(new SubagentRow(
                session.Id,
                session.Title,
                session.Agent,
                StatusText(session.Status),
                FormatAge(now - session.UpdatedAt),
                session.ParentSessionId));
        }

        return rows;
    }

    /// <summary>
    ///     Short display text for a session status (same vocabulary as
    ///     <c>SessionContext.StatusText</c>: running / done / error / aborted / idle).
    /// </summary>
    public static string StatusText(SessionStatus status) => status switch
    {
        SessionStatus.Working => "running",
        SessionStatus.Done => "done",
        SessionStatus.Error => "error",
        SessionStatus.Aborted => "aborted",
        _ => "idle",
    };

    /// <summary>Compact age text: seconds, minutes, hours, or days (never negative).</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(1))
            return $"{(int)age.TotalSeconds}s";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    /// <summary>
    ///     Read-only transcript rows for one sub-agent session: one header plus
    ///     one single-line row per message (user prompt, assistant prose or
    ///     tool-call names, tool results). Never truncates history — the panel
    ///     slices the viewport window itself.
    /// </summary>
    /// <param name="title">Session title for the header row.</param>
    /// <param name="messages">Chronological message history (store snapshot).</param>
    /// <param name="width">Available width for single-line truncation.</param>
    public static List<string> TranscriptRows(string title, IReadOnlyList<AgentMessage> messages, int width)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var rows = new List<string>(messages.Count + 2);
        rows.Add($"{PanelText.Truncate(title, Math.Max(1, width - 14))} (read-only)");
        rows.Add(PanelText.Separator);
        if (messages.Count == 0)
        {
            rows.Add("(no messages yet)");
            return rows;
        }

        int budget = Math.Max(1, width - 10);
        for (int i = 0; i < messages.Count; i++)
        {
            // #461: role + body come from ONE dispatch now — they used to be two
            // hand-rolled switches over the same message, each with a silent
            // "?" / empty-string arm for a role the panel did not know.
            (string role, string body) = new TranscriptRowVisitor().Accept(messages[i]);
            rows.Add($"{role}: {PanelText.Truncate(body, budget)}".TrimEnd());
        }

        return rows;
    }

    /// <summary>Message-level arm of the transcript row: role label + single-line body.</summary>
    private sealed class TranscriptRowVisitor : AgentMessageVisitor<(string Role, string Body)>
    {
        public override (string Role, string Body) Visit(UserMessage message) =>
            ("you", PanelText.SingleLine(message.Content));

        public override (string Role, string Body) Visit(AssistantMessage message) =>
            ("agent", SummarizeAssistant(message));

        public override (string Role, string Body) Visit(ToolResultMessage message) =>
            ("tool", SummarizeToolResults(message));

        private static string SummarizeAssistant(AssistantMessage assistant)
        {
            var visitor = new AssistantSummaryVisitor();
            visitor.Walk(assistant.Parts);
            return visitor.Summary;
        }

        private static string SummarizeToolResults(ToolResultMessage tool)
        {
            var results = tool.Results;
            if (results.Count == 0)
                return "(no results)";
            if (results.Count == 1)
                return SummarizeOneResult(results[0]);
            int errors = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].IsError)
                    errors++;
            }

            return errors == 0
                ? $"({results.Count} results, ok)"
                : $"({results.Count} results, {errors} error(s))";
        }

        private static string SummarizeOneResult(ToolResultEntry result)
        {
            string verdict = result.IsError ? "error" : "ok";
            string output = PanelText.SingleLine(result.Output);
            return string.IsNullOrEmpty(output)
                ? $"{result.ToolName}: {verdict}"
                : $"{result.ToolName}: {verdict} {output}";
        }
    }

    /// <summary>
    ///     Part-level arm of the assistant summary (#461): prose is joined into
    ///     one line, tool calls fall back to their names, and the two kinds that
    ///     have no place in a read-only one-line row are explicit no-ops.
    /// </summary>
    private sealed class AssistantSummaryVisitor : ContentPartVisitor<AssistantSummaryVisitor>
    {
        private string? _joined;
        private List<string>? _toolNames;

        internal string Summary
        {
            get
            {
                if (!string.IsNullOrEmpty(_joined))
                    return _joined;
                if (_toolNames is { Count: > 0 })
                    return $"[tool_call: {string.Join(", ", _toolNames)}]";
                return "(no text)";
            }
        }

        public override AssistantSummaryVisitor Visit(TextPart part)
        {
            if (!string.IsNullOrWhiteSpace(part.Text))
            {
                string single = PanelText.SingleLine(part.Text);
                _joined = _joined is null ? single : $"{_joined} {single}";
            }

            return this;
        }

        public override AssistantSummaryVisitor Visit(ToolCallPart part)
        {
            (_toolNames ??= new List<string>(2)).Add(part.ToolName);
            return this;
        }

        /// <summary>Reasoning is not part of a one-line transcript row.</summary>
        public override AssistantSummaryVisitor Visit(ThinkingPart part) => this;

        /// <inheritdoc cref="Visit(ThinkingPart)" />
        public override AssistantSummaryVisitor Visit(FilePart part) => this;
    }


}
