namespace Harbor.Application.Hooks;

/// <summary>
///     A hook command's decision for the work it was asked about (a tool call
///     or a prompt).
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
/// <remarks>
///     <para>
///         The three trailing members were added by slice 2 for the session
///         and prompt events, and are null on every slice-1 event — so a hook
///         written against slice 1 keeps working, and one written against
///         slice 2 reads them by name (never by position).
///     </para>
///     <para>
///         They are <c>init</c> properties rather than positional parameters
///         deliberately: a positional record's parameters are order-bound, and
///         appending three to the tail would silently re-map any construction
///         that passed fewer.
///     </para>
/// </remarks>
public sealed record HookPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("args")] JsonElement? Args,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("sessionId")] string SessionId)
{
    /// <summary>Agent name driving the run (SessionStart / UserPromptSubmit / PreCompact); null elsewhere.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>Resolved model id for the run; null on slice-1 events and when unresolved.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>The user's prompt text (<see cref="HookEvents.UserPromptSubmit" /> only); null elsewhere.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }
}

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
///     Runs user hook commands (PX4, slices 1–2). Plain shell commands from
///     <c>~/.harbor/hooks.json</c> — no plugin machinery, no Roslyn, no
///     <c>Assembly.Load</c>.
/// </summary>
/// <remarks>
///     Fail-closed where it matters: <c>PreToolUse</c> and
///     <c>UserPromptSubmit</c> deny when a hook times out, cannot start,
///     prints invalid JSON, or names an unknown decision.
///     <c>PostToolUse</c>/<c>SessionStart</c>/<c>PreCompact</c>/<c>SessionEnd</c>
///     are advisory — hook failures are logged and never fail the run. No
///     method here throws except on a cancelled <see cref="CancellationToken" />.
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

    /// <summary>
    ///     Run every <c>SessionStart</c> hook once, after the run's start
    ///     event. Advisory, same contract as <see cref="RunPostToolUseAsync" />.
    /// </summary>
    /// <param name="sessionId">Owning session id.</param>
    /// <param name="agent">Agent name driving the run; rides the payload as <c>agent</c>.</param>
    /// <param name="model">Resolved model id; rides the payload as <c>model</c>.</param>
    Task RunSessionStartAsync(
        string sessionId, string? agent = null, string? model = null, CancellationToken ct = default);

    /// <summary>
    ///     Run every <c>UserPromptSubmit</c> hook for one prompt. A gate,
    ///     fail-closed on the same terms as <see cref="RunPreToolUseAsync" /> —
    ///     but there are no args to edit, so <c>edit</c>/<c>editedArgs</c> are
    ///     ignored here by design (an unedited verdict still means allow).
    /// </summary>
    /// <param name="sessionId">Owning session id.</param>
    /// <param name="prompt">The user's prompt text.</param>
    /// <param name="agent">Agent name the prompt was addressed to.</param>
    /// <param name="model">Resolved model id.</param>
    Task<HookVerdict> RunUserPromptSubmitAsync(
        string sessionId, string prompt, string? agent = null, string? model = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Run every <c>PreCompact</c> hook once, before compaction summarizes
    ///     history. Advisory, same contract as <see cref="RunPostToolUseAsync" />:
    ///     a hook observes the shrink, it cannot veto or delay it.
    /// </summary>
    /// <param name="sessionId">Owning session id.</param>
    /// <param name="agent">Agent name driving the run.</param>
    /// <param name="model">Resolved model id whose context window triggered the pass.</param>
    Task RunPreCompactAsync(
        string sessionId, string? agent = null, string? model = null, CancellationToken ct = default);
}
