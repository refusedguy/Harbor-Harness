using Harbor.Abstractions.Models;

namespace Harbor.Application.Sessions;

/// <summary>
///     The repair for a tool call that never received a result (#1751).
/// </summary>
/// <remarks>
///     <para>
///         A <c>tool_use</c> without a matching <c>tool_result</c> is a provider
///         error: Anthropic requires every <c>tool_use</c> to be answered in the
///         next message, OpenAI requires a <c>tool</c> role message with the same
///         id. A session that carries one cannot be resumed — the next request
///         fails and the transcript stays invalid for the provider.
///     </para>
///     <para>
///         <b>Two codes, two retry policies.</b> The code is not decoration: it
///         tells the model whether the call may be repeated.
///         <list type="bullet">
///             <item>
///                 <term><see cref="UnresolvedToolCall.NotStartedCode" /></term>
///                 <description>
///                     The call never began executing. It had no effect, so it is
///                     safe to retry. This is what the runtime knows for sure:
///                     the turn closed before the dispatcher was reached.
///                 </description>
///             </item>
///             <item>
///                 <term><see cref="UnresolvedToolCall.OutcomeUnknownCode" /></term>
///                 <description>
///                     The call may have run and its outcome was never captured
///                     (the process was killed mid-call, or the transcript was
///                     written by an older build). The transcript cannot prove
///                     which, so the repair says the honest thing: retry only
///                     when the operation is read-only or idempotent.
///                 </description>
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>One owner for the text.</b> The writer (the turn runner, in
///         <c>Harbor.AgentRuntime</c>) and the repairer (the message
///         converter, beside this file) must hand the model the same
///         vocabulary, so both build it here — one literal, one place.
///         The type is public for that reason: the namespace is
///         <c>Harbor.Application.Sessions</c> (unchanged by the #1624
///         extraction, like every other member of this cohort) and the
///         turn runner lives in another assembly.
///     </para>
/// </remarks>
public static class UnresolvedToolCallRepair
{
    /// <summary>
    ///     The text the model reads back for a call that never started: it had
    ///     no effect, so the retry is safe.
    /// </summary>
    /// <param name="toolName">The tool the model asked for.</param>
    /// <returns>The model-facing explanation, carrying the code.</returns>
    public static string NotStartedText(string toolName) => UnresolvedToolCall.NotStartedText(toolName);

    /// <summary>
    ///     The text the model reads back for a call whose outcome is unknown:
    ///     it may have taken effect, so a blind retry can apply it twice.
    /// </summary>
    /// <param name="toolName">The tool the model asked for.</param>
    /// <returns>The model-facing explanation, carrying the code.</returns>
    public static string OutcomeUnknownText(string toolName) => UnresolvedToolCall.OutcomeUnknownText(toolName);

    /// <summary>
    ///     The model-facing text for a code.
    /// </summary>
    /// <param name="code">One of the <see cref="UnresolvedToolCall" /> codes.</param>
    /// <param name="toolName">The tool the model asked for.</param>
    /// <returns>The model-facing explanation, carrying the code.</returns>
    public static string TextFor(string code, string toolName) => UnresolvedToolCall.TextFor(code, toolName);

    /// <summary>
    ///     Build the tool results that answer the calls an assistant message
    ///     opened and the turn never closed.
    /// </summary>
    /// <remarks>
    ///     #1760: every entry carries the structured code, so a policy reads
    ///     the code instead of the text. The text already leads with it (see
    ///     <see cref="UnresolvedToolCall" />), so the two say the same thing.
    /// </remarks>
    /// <param name="assistant">The assistant message whose calls went unanswered.</param>
    /// <param name="code">One of the <see cref="UnresolvedToolCall" /> codes.</param>
    /// <returns>
    ///     A tool-result message carrying one error entry per unanswered call,
    ///     or <see langword="null" /> when the assistant message asked for no
    ///     tool at all (nothing to close).
    /// </returns>
    public static ToolResultMessage? Close(AssistantMessage assistant, string code)
    {
        List<ToolResultEntry>? results = null;
        for (int i = 0; i < assistant.Parts.Count; i++)
        {
            if (assistant.Parts[i] is not ToolCallPart call)
            {
                continue;
            }

            results ??= new List<ToolResultEntry>();
            results.Add(new ToolResultEntry(call.Id, call.ToolName, TextFor(code, call.ToolName), true)
            {
                ErrorInfo = ToolErrorCodes.Create(code),
            });
        }

        if (results is null)
        {
            return null;
        }

        return new ToolResultMessage(
            Guid.NewGuid().ToString("N"),
            assistant.SessionId,
            DateTimeOffset.UtcNow,
            results);
    }
}
