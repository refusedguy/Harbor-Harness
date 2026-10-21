namespace Harbor.Abstractions.Models;

/// <summary>
///     The vocabulary of structured tool-error codes (#1760).
/// </summary>
/// <remarks>
///     <para>
///         A code is a stable promise: the spelling does not change, and a
///         policy may branch on it. The human message that accompanies a code
///         is free text and may be reworded at any time — that is the whole
///         point of the split.
///     </para>
///     <para>
///         <b>Which codes exist.</b> Each one names a fact that a caller
///         might have to act on differently:
///     </para>
///     <list type="table">
///         <item>
///             <term><see cref="Timeout" /></term>
///             <description>The call's own deadline fired. Retrying may help.</description>
///         </item>
///         <item>
///             <term><see cref="AbortedBeforeDispatch" /></term>
///             <description>The call was never sent to the tool. It had no effect; a retry is safe.</description>
///         </item>
///         <item>
///             <term><see cref="NotStarted" /></term>
///             <description>The call never began (the turn closed first). It had no effect; a retry is safe.</description>
///         </item>
///         <item>
///             <term><see cref="OutcomeUnknown" /></term>
///             <description>
///                 The call may have run and its outcome was never captured. A blind retry
///                 can apply it twice.
///             </description>
///         </item>
///         <item>
///             <term><see cref="NotFound" /></term>
///             <description>No such tool is registered (or the name is not a valid tool name).</description>
///         </item>
///         <item>
///             <term><see cref="ValidationFailed" /></term>
///             <description>The arguments failed the tool's own validation.</description>
///         </item>
///         <item>
///             <term><see cref="PermissionDenied" /></term>
///             <description>The permission policy refused the call. A decision, not a malfunction.</description>
///         </item>
///         <item>
///             <term><see cref="PermissionFailed" /></term>
///             <description>
///                 The permission subsystem itself failed (the agent is unknown, the service threw).
///                 Distinct from <see cref="PermissionDenied" />: nothing was decided.
///             </description>
///         </item>
///         <item>
///             <term><see cref="HookDenied" /></term>
///             <description>A user PreToolUse hook refused the call.</description>
///         </item>
///         <item>
///             <term><see cref="Cancelled" /></term>
///             <description>The run was cancelled while the call was in flight.</description>
///         </item>
///         <item>
///             <term><see cref="Abandoned" /></term>
///             <description>
///                 The call outlived the accepted stop and was abandoned at the dispatch boundary.
///                 Its outcome is unknown, and the transcript says so rather than guessing.
///             </description>
///         </item>
///         <item>
///             <term><see cref="ExecutionFailed" /></term>
///             <description>The tool threw.</description>
///         </item>
///         <item>
///             <term><see cref="MalformedArguments" /></term>
///             <description>The model emitted arguments that are not valid JSON. The tool never ran.</description>
///         </item>
///         <item>
///             <term><see cref="Error" /></term>
///             <description>
///                 The tool reported a failure and did not classify it further. The generic
///                 code — a tool that knows better returns its own.
///             </description>
///         </item>
///     </list>
///     <para>
///         <b>Unknown codes are legal.</b> A plugin may return a code this
///         build has never heard of. It is carried verbatim, it reaches the
///         model, and it survives storage; <see cref="IsKnown" /> only answers
///         whether THIS build recognises it.
///     </para>
/// </remarks>
public static class ToolErrorCodes
{
    /// <summary>
    ///     The tool reported a failure and did not classify it further. The
    ///     generic code — a tool that knows better returns its own.
    /// </summary>
    public const string Error = "TOOL_ERROR";

    /// <summary>
    ///     The call's own deadline fired. The tool may or may not have had
    ///     partial effects; a retry is usually worth trying.
    /// </summary>
    public const string Timeout = "TOOL_TIMEOUT";

    /// <summary>
    ///     The call was never sent to the tool (the run was cancelled, or the
    ///     commit barrier rejected it). It had no effect, so a retry is safe.
    /// </summary>
    public const string AbortedBeforeDispatch = "TOOL_ABORTED_BEFORE_DISPATCH";

    /// <summary>
    ///     The call never began executing (the turn closed before the
    ///     dispatcher was reached). It had no effect, so a retry is safe.
    /// </summary>
    public const string NotStarted = UnresolvedToolCall.NotStartedCode;

    /// <summary>
    ///     The call may have run and its outcome was never captured. Retry only
    ///     when the operation is read-only or idempotent.
    /// </summary>
    public const string OutcomeUnknown = UnresolvedToolCall.OutcomeUnknownCode;

    /// <summary>
    ///     No such tool is registered, or the name is not a valid tool name.
    /// </summary>
    public const string NotFound = "TOOL_NOT_FOUND";

    /// <summary>
    ///     The arguments failed the tool's own validation. The tool never ran.
    /// </summary>
    public const string ValidationFailed = "TOOL_VALIDATION_FAILED";

    /// <summary>
    ///     The permission policy refused the call. A decision, not a malfunction.
    /// </summary>
    public const string PermissionDenied = "TOOL_PERMISSION_DENIED";

    /// <summary>
    ///     The permission subsystem itself failed, so nothing was decided.
    ///     Distinct from <see cref="PermissionDenied" />.
    /// </summary>
    public const string PermissionFailed = "TOOL_PERMISSION_FAILED";

    /// <summary>
    ///     A user PreToolUse hook refused the call.
    /// </summary>
    public const string HookDenied = "TOOL_HOOK_DENIED";

    /// <summary>
    ///     The run was cancelled while the call was in flight.
    /// </summary>
    public const string Cancelled = "TOOL_CANCELLED";

    /// <summary>
    ///     The call outlived the accepted stop and was abandoned at the
    ///     dispatch boundary.
    /// </summary>
    public const string Abandoned = "TOOL_ABANDONED";

    /// <summary>
    ///     The tool threw.
    /// </summary>
    public const string ExecutionFailed = "TOOL_EXECUTION_FAILED";

    /// <summary>
    ///     The model emitted arguments that are not valid JSON. The tool never ran.
    /// </summary>
    public const string MalformedArguments = "TOOL_MALFORMED_ARGUMENTS";

    /// <summary>
    ///     Whether this build recognises the code as one of its own.
    /// </summary>
    /// <remarks>
    ///     A <see langword="false" /> answer is not a rejection: an unknown
    ///     code is carried verbatim (see <see cref="ToolErrorInfo.Code" />).
    ///     It only says "this build has no name and no policy for it".
    /// </remarks>
    /// <param name="code">The code to classify.</param>
    /// <returns><see langword="true" /> when the code is part of the vocabulary.</returns>
    public static bool IsKnown(string? code) => code switch
    {
        Error => true,
        Timeout => true,
        AbortedBeforeDispatch => true,
        NotStarted => true,
        OutcomeUnknown => true,
        NotFound => true,
        ValidationFailed => true,
        PermissionDenied => true,
        PermissionFailed => true,
        HookDenied => true,
        Cancelled => true,
        Abandoned => true,
        ExecutionFailed => true,
        MalformedArguments => true,
        _ => false,
    };

    /// <summary>
    ///     The canonical name for a code — the class-like label that rides
    ///     beside it in <see cref="ToolErrorInfo.Name" />.
    /// </summary>
    /// <param name="code">The code to name.</param>
    /// <returns>
    ///     The canonical name, or <c>ToolError</c> for a code this build does
    ///     not know — an unknown code still gets a name, so the shape of the
    ///     record never depends on the vocabulary.
    /// </returns>
    public static string NameFor(string? code) => code switch
    {
        Error => "ToolFailedError",
        Timeout => "ToolTimeoutError",
        AbortedBeforeDispatch => "AbortError",
        NotStarted => "ToolNotStartedError",
        OutcomeUnknown => "ToolOutcomeUnknownError",
        NotFound => "ToolNotFoundError",
        ValidationFailed => "ToolValidationError",
        PermissionDenied => "PermissionDeniedError",
        PermissionFailed => "PermissionFailedError",
        HookDenied => "ToolHookDeniedError",
        Cancelled => "ToolCancelledError",
        Abandoned => "ToolAbandonedError",
        ExecutionFailed => "ToolExecutionError",
        MalformedArguments => "MalformedToolCallError",
        _ => "ToolError",
    };

    /// <summary>
    ///     Build the structured error for a code, with the canonical name and
    ///     an optional reason.
    /// </summary>
    /// <param name="code">The stable code.</param>
    /// <param name="reason">Optional detail (the deadline, the exception message, the policy reason).</param>
    /// <returns>The structured error.</returns>
    public static ToolErrorInfo Create(string code, string? reason = null) =>
        new(NameFor(code), code, reason);

    /// <summary>
    ///     The model-facing prefix: the code, then the human detail. Every
    ///     error output starts with its code, so the model reads a code and
    ///     not a prose puzzle.
    /// </summary>
    /// <param name="code">The stable code.</param>
    /// <param name="detail">The human-readable detail.</param>
    /// <returns>The composed text.</returns>
    public static string Format(string code, string detail) => $"{code}: {detail}";
}
