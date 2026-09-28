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

    private static string RenderOne(AgentMessage message) => message switch
    {
        UserMessage user => "[user] " + Truncate(user.Content),
        AssistantMessage assistant => "[assistant] " + Truncate(AssistantText(assistant)),
        ToolResultMessage results => "[tool_result " + ToolResultSummary(results) + "]",
        _ => "[" + message.Role + "]"
    };

    private static string AssistantText(AssistantMessage assistant)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < assistant.Parts.Count; i++)
        {
            switch (assistant.Parts[i])
            {
                case TextPart text when !string.IsNullOrEmpty(text.Text):
                    sb ??= new StringBuilder();
                    sb.Append(text.Text);
                    break;
                case ToolCallPart call:
                    sb ??= new StringBuilder();
                    sb.Append("[tool:").Append(call.ToolName).Append(']');
                    break;
            }
        }

        return sb?.ToString() ?? string.Empty;
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
