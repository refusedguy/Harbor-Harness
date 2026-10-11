namespace Harbor.Application.Hooks;

/// <summary>
///     A hook command's decision for one tool call.
/// </summary>
public enum HookDecision
{
    /// <summary>Let the tool call proceed (possibly with edited args).</summary>
    Allow,

    /// <summary>Block the tool call with an error entry.</summary>
    Deny,

    /// <summary>Ask the user (routes to the permission asker; denies headless).</summary>
    Ask,
}

/// <summary>
///     Merged verdict of every matching hook for one event.
/// </summary>
/// <param name="Decision">The merged decision.</param>
/// <param name="Reason">Human-readable reason (deny/ask), shown to the model and user.</param>
/// <param name="EditedArgs">
///     Replacement tool args when a hook edited them (an owned clone, safe to
///     keep past the hook process lifetime). Null when no hook edited args.
/// </param>
public sealed record HookVerdict(HookDecision Decision, string? Reason, JsonElement? EditedArgs)
{
    /// <summary>Shared allow verdict for the no-hooks fast path (no allocation per call).</summary>
    public static readonly HookVerdict Allow = new(HookDecision.Allow, null, null);
}

/// <summary>
///     Payload delivered to a hook command on stdin as JSON.
/// </summary>
/// <param name="Event">Event name (<see cref="HookEvents" />).</param>
/// <param name="Tool">Tool name (Pre/PostToolUse); null for SessionEnd.</param>
/// <param name="Args">Raw tool-call args; null for SessionEnd.</param>
/// <param name="Result">Tool output text (PostToolUse only); null otherwise.</param>
/// <param name="SessionId">Owning session id.</param>
public sealed record HookPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("args")] JsonElement? Args,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("sessionId")] string SessionId);

/// <summary>
///     Verdict a hook command prints on stdout as JSON.
///     <c>{"decision":"allow"}</c>, <c>{"decision":"deny","reason":"..."}</c>,
///     <c>{"decision":"ask","reason":"..."}</c>,
///     <c>{"decision":"edit","editedArgs":{...}}</c>.
///     Empty output with exit 0 also means allow (side-effect-only hooks).
/// </summary>
/// <param name="Decision">allow/deny/ask/edit (case-insensitive).</param>
/// <param name="Reason">Why (deny/ask).</param>
/// <param name="EditedArgs">Replacement args (edit, or allow-with-edit).</param>
public sealed record HookVerdictDto(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("editedArgs")] JsonElement? EditedArgs);

/// <summary>
///     Runs user hook commands (PX4, slice 1). Plain shell commands from
///     <c>~/.harbor/hooks.json</c> — no plugin machinery, no Roslyn, no
///     <c>Assembly.Load</c>.
/// </summary>
/// <remarks>
///     Fail-closed where it matters: <c>PreToolUse</c> denies when a hook
///     times out, cannot start, prints invalid JSON, or names an unknown
///     decision. <c>PostToolUse</c>/<c>SessionEnd</c> are advisory — hook
///     failures are logged and never fail the run. No method here throws
///     except on a cancelled <see cref="CancellationToken" />.
/// </remarks>
public interface IHookRunner
{
    /// <summary>
    ///     Run matching <c>PreToolUse</c> hooks for one tool call and merge
    ///     their verdicts: the first deny wins immediately; ask is recorded
    ///     and a later deny overrides it; edits accumulate into the returned
    ///     args and are visible to later hooks.
    /// </summary>
    Task<HookVerdict> RunPreToolUseAsync(
        string toolName, JsonElement args, string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Run matching <c>PostToolUse</c> hooks. Advisory: verdicts are
    ///     ignored, failures only logged. Never throws on its own.
    /// </summary>
    Task RunPostToolUseAsync(
        string toolName, JsonElement args, ToolResult result, string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Run every <c>SessionEnd</c> hook. Advisory, same contract as
    ///     <see cref="RunPostToolUseAsync" />.
    /// </summary>
    Task RunSessionEndAsync(string sessionId, CancellationToken ct = default);
}
