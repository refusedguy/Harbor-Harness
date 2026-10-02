using System.Text;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Shared protocol for cross-session peer supervision (issue #165).
///     Sessions are PEERS (unlike parent→child <c>task</c> sub-agents): they run
///     in parallel and may inspect each other (<c>session_read</c>) and deliver
///     directives (<c>session_steer</c>).
/// </summary>
/// <remarks>
///     <para>
///         Provenance: every steered message carries a machine-readable trailer
///         <c>[steer-from:&lt;sessionId&gt;]</c> (same trailer idea as
///         <c>SubAgentFailureFormat.ResumeMarker</c>). The trailer lets
///         <c>session_steer</c> enforce depth-1 (a session cannot steer its own
///         supervisor) and lets <c>session_read</c> report who steered a session.
///     </para>
///     <para>
///         Peer operations over <c>session_read</c>/<c>session_steer</c>:
///         <c>message</c> (ping/context/result), <c>redirect</c> (change the
///         neighbor's task), <c>restart</c> (re-run with the same context).
///         Delivery is durable (appended to the neighbor's store, picked up on
///         its next run) — it never interrupts a live run.
///     </para>
/// </remarks>
internal static class SessionSupervision
{
    /// <summary>Machine-readable steer-provenance trailer: <c>[steer-from:&lt;sessionId&gt;]</c>.</summary>
    public const string SteerMarkerPrefix = "[steer-from:";

    public const string OpMessage = "message";
    public const string OpRedirect = "redirect";
    public const string OpRestart = "restart";

    /// <summary>Maximum transcript messages returned by default.</summary>
    public const int DefaultTailLimit = 20;

    /// <summary>Hard cap for the transcript tail (bounds prompt injection surface).</summary>
    public const int MaxTailLimit = 50;

    /// <summary>Per-entry truncation budget for rendered transcripts.</summary>
    public const int MaxCharsPerEntry = 1200;

    /// <summary>Whether <paramref name="operation" /> names a known peer operation.</summary>
    public static bool IsKnownOperation(string operation) =>
        operation == OpMessage || operation == OpRedirect || operation == OpRestart;

    /// <summary>
    ///     Collect distinct supervisor session ids from steer trailers in
    ///     <paramref name="messages" /> (caller-side scan for the depth-1 guard).
    ///     Only user-message content is scanned — the tool is the sole author
    ///     of the trailer, always on a <see cref="UserMessage" />.
    /// </summary>
    public static IReadOnlyList<string> FindSteerAuthors(IReadOnlyList<AgentMessage> messages)
    {
        List<string>? authors = null;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not UserMessage user || string.IsNullOrEmpty(user.Content))
                continue;
            ExtractAuthors(user.Content, ref authors);
        }

        return authors ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    ///     Infer a terminal run outcome from stored history using the same
    ///     last-assistant mapping as <see cref="RunOutcome.Reconstruct" />:
    ///     Aborted → stopped, Error → failed, otherwise succeeded.
    ///     Empty history (or no assistant message yet) → unknown.
    /// </summary>
    /// <remarks>
    ///     #993: this mapping and <see cref="RunOutcome.Reconstruct" /> used to
    ///     disagree on exactly the case they are both written to describe — no
    ///     assistant message. This one answered "unknown"; Reconstruct's catch-all
    ///     answered "succeeded", while its doc claimed they were the same mapping.
    ///     Reconstruct now agrees: no terminal assistant message is never a success.
    ///     If you change one, change both.
    /// </remarks>
    public static string InferOutcome(IReadOnlyList<AgentMessage> messages)
    {
        AssistantMessage? last = null;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage assistant)
            {
                last = assistant;
                break;
            }
        }

        if (last is null)
            return "unknown";
        if (last.StopReason == StopReason.Aborted)
            return "stopped";
        if (last.StopReason == StopReason.Error)
            return "failed";
        return "succeeded";
    }

    /// <summary>
    ///     Render the tail of a transcript compactly: one line per message
    ///     (<c>[user]</c> / <c>[assistant]</c> / <c>[tool_result ok|error]</c>),
    ///     each entry truncated to <see cref="MaxCharsPerEntry" />.
    /// </summary>
    public static string RenderTail(IReadOnlyList<AgentMessage> messages, int limit)
    {
        int take = Math.Min(limit, messages.Count);
        var sb = new StringBuilder(2048);
        for (int i = messages.Count - take; i < messages.Count; i++)
        {
            sb.Append(RenderOne(messages[i]));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Build the persisted content of a steered peer message (human line + machine trailer).</summary>
    public static string BuildSteerContent(string supervisorSessionId, string operation, string instruction) =>
        operation switch
        {
            OpRedirect =>
                $"[peer redirect from {supervisorSessionId} — new direction, supersedes the current task]: {instruction} {SteerMarkerPrefix}{supervisorSessionId}]",
            OpRestart =>
                $"[peer restart from {supervisorSessionId} — re-run with the same context]: {instruction} {SteerMarkerPrefix}{supervisorSessionId}]",
            _ =>
                $"[peer message from {supervisorSessionId}]: {instruction} {SteerMarkerPrefix}{supervisorSessionId}]"
        };

    /// <summary>
    ///     Renders one transcript entry. #461: the per-kind dispatch lives in
    ///     <see cref="RenderOneVisitor" />; an unknown role now throws instead of
    ///     being printed as a bare <c>[role]</c> by a <c>default:</c> arm.
    /// </summary>
    private static string RenderOne(AgentMessage message) => new RenderOneVisitor().Accept(message);

    private sealed class RenderOneVisitor : AgentMessageVisitor<string>
    {
        public override string Visit(UserMessage message) => "[user] " + Truncate(message.Content);

        public override string Visit(AssistantMessage message) => "[assistant] " + Truncate(AssistantText(message));

        public override string Visit(ToolResultMessage message) => "[tool_result " + ToolResultSummary(message) + "]";

        /// <summary>
        ///     Flattens an assistant turn into one line: its text plus a
        ///     <c>[tool:name]</c> marker per tool call, lazily built so a turn with
        ///     nothing renderable still returns <see cref="string.Empty" />.
        /// </summary>
        private static string AssistantText(AssistantMessage assistant)
        {
            var visitor = new AssistantTextVisitor();
            visitor.Walk(assistant.Parts);
            return visitor.Text;
        }

        private static string ToolResultSummary(ToolResultMessage results)
        {
            int errors = 0;
            for (int i = 0; i < results.Results.Count; i++)
            {
                if (results.Results[i].IsError)
                    errors++;
            }

            return errors == 0 ? $"ok ({results.Results.Count})" : $"error {errors}/{results.Results.Count}";
        }

        private static string Truncate(string text)
        {
            if (text.Length <= MaxCharsPerEntry)
                return text;
            return text.Substring(0, MaxCharsPerEntry) + $"…[truncated {text.Length - MaxCharsPerEntry} chars]";
        }
    }

    /// <summary>
    ///     Part-level arm of the transcript renderer (#461). The empty-text guard
    ///     and the lazy builder are preserved exactly: an assistant turn made only
    ///     of blank text parts still renders as <see cref="string.Empty" />.
    /// </summary>
    private sealed class AssistantTextVisitor : ContentPartVisitor<AssistantTextVisitor>
    {
        private StringBuilder? _sb;

        internal string Text => _sb?.ToString() ?? string.Empty;

        public override AssistantTextVisitor Visit(TextPart part)
        {
            if (!string.IsNullOrEmpty(part.Text))
            {
                (_sb ??= new StringBuilder()).Append(part.Text);
            }

            return this;
        }

        public override AssistantTextVisitor Visit(ToolCallPart part)
        {
            (_sb ??= new StringBuilder()).Append("[tool:").Append(part.ToolName).Append(']');
            return this;
        }

        /// <summary>
        ///     Reasoning is not part of the supervisor's one-line picture of the
        ///     turn, and a file part has no place in a 1-line summary — both were
        ///     silent gaps in the old switch, now explicit.
        /// </summary>
        public override AssistantTextVisitor Visit(ThinkingPart part) => this;

        /// <inheritdoc cref="Visit(ThinkingPart)" />
        public override AssistantTextVisitor Visit(FilePart part) => this;
    }

    private static void ExtractAuthors(string content, ref List<string>? authors)
    {
        int from = 0;
        while (true)
        {
            int marker = content.IndexOf(SteerMarkerPrefix, from, StringComparison.Ordinal);
            if (marker < 0)
                return;
            int idStart = marker + SteerMarkerPrefix.Length;
            int idEnd = content.IndexOf(']', idStart);
            if (idEnd > idStart)
            {
                string id = content.Substring(idStart, idEnd - idStart);
                if (id.Length > 0 && id.IndexOfAny([' ', '[', ']']) < 0)
                {
                    authors ??= new List<string>(2);
                    if (!authors.Contains(id))
                        authors.Add(id);
                }

                from = idEnd + 1;
            }
            else
            {
                return;
            }
        }
    }
}
