namespace Harbor.Application.Hooks;

/// <summary>
///     User-hook event points (PX4, slice 1 + slice 2). Each name is a key in
///     <c>~/.harbor/hooks.json</c> under <c>"hooks"</c> and a call site on
///     <see cref="IHookRunner" />. Claude-parity naming.
///     <para>
///         Two shapes exist and the difference is load-bearing:
///         <list type="bullet">
///             <item>
///                 <b>Gates</b> — <see cref="PreToolUse" />,
///                 <see cref="UserPromptSubmit" />. The merged verdict decides
///                 whether the work happens; the runner fails CLOSED (a hook
///                 that times out, cannot start, prints invalid JSON or names
///                 an unknown decision denies the work).
///             </item>
///             <item>
///                 <b>Advisory</b> — <see cref="PostToolUse" />,
///                 <see cref="SessionStart" />, <see cref="PreCompact" />,
///                 <see cref="SessionEnd" />. The verdict is logged and
///                 discarded; a hook can never fail or rewrite the run it
///                 observes. Same "observable, never authoritative" contract.
///             </item>
///         </list>
///     </para>
/// </summary>
public static class HookEvents
{
    /// <summary>Runs before a tool executes; may allow, deny, ask, or edit args.</summary>
    public const string PreToolUse = "PreToolUse";

    /// <summary>Runs after a tool executes; advisory, the verdict is ignored.</summary>
    public const string PostToolUse = "PostToolUse";

    /// <summary>Runs once when an agent run ends (normal or cancelled); advisory.</summary>
    public const string SessionEnd = "SessionEnd";

    /// <summary>
    ///     Runs once when an agent run starts, after <c>AgentStartEvent</c>;
    ///     advisory. Carries the owning agent and resolved model so a hook can
    ///     record what a run is about to use without reading the session store.
    /// </summary>
    public const string SessionStart = "SessionStart";

    /// <summary>
    ///     Runs once before the first turn with the user's prompt; a gate. A
    ///     deny fails the run before any model call, exactly like a
    ///     <see cref="PreToolUse" /> deny fails one tool call.
    /// </summary>
    public const string UserPromptSubmit = "UserPromptSubmit";

    /// <summary>
    ///     Runs once before compaction summarizes history; advisory. A hook
    ///     observes a context window about to shrink — it cannot delay, veto
    ///     or rewrite the summary.
    /// </summary>
    public const string PreCompact = "PreCompact";
}
