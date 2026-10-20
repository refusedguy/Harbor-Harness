namespace Harbor.Abstractions.Models;

/// <summary>
///     The two codes a synthesized <c>tool_result</c> carries when a turn closes
///     with a tool call that never got an answer (#1751).
/// </summary>
/// <remarks>
///     <para>
///         A <c>tool_use</c> without a matching <c>tool_result</c> is a provider
///         error, not a cosmetic gap: Anthropic requires every <c>tool_use</c> to
///         be answered in the next message and OpenAI requires a <c>tool</c> role
///         message with the same id. A session that carries one cannot be
///         resumed — the next request fails and the transcript stays invalid.
///     </para>
///     <para>
///         The two codes are not cosmetic. They answer the one question that
///         matters when the model reads one back: may this call be retried?
///     </para>
///     <list type="bullet">
///         <item>
///             <term><see cref="NotStartedCode" /></term>
///             <description>
///                 The call never began executing. It had no effect, so it is
///                 safe to retry.
///             </description>
///         </item>
///         <item>
///             <term><see cref="OutcomeUnknownCode" /></term>
///             <description>
///                 The call may have run; its outcome was never captured. Retry
///                 only when the operation is read-only or idempotent — if it
///                 may have side effects, verify the external state or ask the
///                 user first.
///             </description>
///         </item>
///     </list>
///     <para>
///         The unknown case is the sibling of
///         <see cref="RecoveryBoundary.InFlightToolCall" />: the same fact (a
///         call with no result) seen by two audiences. Harbor's own
///         <see cref="RunRecoveryPolicy" /> refuses an automatic resume on it
///         (an unknown result is unknown, even for a read); the code is what
///         the MODEL reads back, and it tells the model the same thing in the
///         model's language — do not retry blindly.
///     </para>
/// </remarks>
public static class UnresolvedToolCall
{
    /// <summary>
    ///     The call never began executing — it had no effect and is safe to retry.
    /// </summary>
    public const string NotStartedCode = "TOOL_NOT_STARTED";

    /// <summary>
    ///     The call may have run and its outcome was never captured — retry only
    ///     when the operation is read-only or idempotent.
    /// </summary>
    public const string OutcomeUnknownCode = "TOOL_OUTCOME_UNKNOWN";

    /// <summary>
    ///     The model-facing text for a call that never started: it had no
    ///     effect, so the retry is safe.
    /// </summary>
    /// <param name="toolName">The tool the model asked for.</param>
    /// <returns>The model-facing explanation, carrying the code.</returns>
    public static string NotStartedText(string toolName) =>
        $"{NotStartedCode}: tool '{toolName}' was never started — the turn ended before it ran. "
        + "The call had no effect and is safe to retry.";

    /// <summary>
    ///     The model-facing text for a call whose outcome is unknown: it may
    ///     have taken effect, so a blind retry can apply it twice.
    /// </summary>
    /// <param name="toolName">The tool the model asked for.</param>
    /// <returns>The model-facing explanation, carrying the code.</returns>
    public static string OutcomeUnknownText(string toolName) =>
        $"{OutcomeUnknownCode}: tool '{toolName}' has no recorded result — the turn ended before its outcome was captured. "
        + "The call may have taken effect: retry only if the operation is read-only or idempotent; "
        + "if it may have side effects, first verify external state or ask the user. Do not retry blindly.";

    /// <summary>
    ///     The model-facing text for a code.
    /// </summary>
    /// <param name="code">One of the <see cref="UnresolvedToolCall" /> codes.</param>
    /// <param name="toolName">The tool the model asked for.</param>
    /// <returns>The model-facing explanation, carrying the code.</returns>
    public static string TextFor(string code, string toolName) =>
        string.Equals(code, NotStartedCode, StringComparison.Ordinal)
            ? NotStartedText(toolName)
            : OutcomeUnknownText(toolName);
}
