using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
namespace Harbor.Abstractions.Agents;
/// <summary>
///     Agent definition. An "agent" combines a model, a permission ruleset, and a max-steps limit.
///     Implements Prototype pattern (GOF) — agents are cloned and customized.
/// </summary>
/// <remarks>
///     <para>
///         An <see cref="AgentDefinition" /> is the static blueprint for an agent run. It is paired
///         with a <see cref="Session" /> at runtime via <see cref="IAgent.Initialize" />. Agents are
///         immutable records: use the <see langword="with" /> expression or the <c>WithModel</c> /
///         <c>WithPermission</c> helpers to derive customized variants.
///     </para>
///     <para>
///         Sub-agents (those with <see cref="IsSubAgent" /> = <see langword="true" />) are invocable
///         from the <c>task</c> tool for delegated work. They run in their own context with their
///         own permission ruleset and a typically tighter step budget.
///     </para>
///     <para>
///         Thread-safety: instances are immutable and safe to share across threads.
///     </para>
/// </remarks>
/// <param name="Name">Stable lowercase identifier (e.g. <c>code</c>, <c>plan</c>).</param>
/// <param name="DisplayName">Human-readable name shown in the TUI status bar.</param>
/// <param name="Description">One-line description shown in <c>/agents</c>.</param>
/// <param name="Model">Model id (without provider prefix).</param>
/// <param name="ProviderId">Owning provider id (e.g. <c>anthropic</c>).</param>
/// <param name="Permission">Permission ruleset applied to every tool call.</param>
/// <param name="MaxSteps">Hard cap on agent-loop iterations. Defaults to 50.</param>
/// <param name="Temperature">Optional sampling temperature override.</param>
/// <param name="ReasoningEffort">Optional reasoning-effort hint for reasoning-aware models.</param>
/// <param name="SystemPromptAppend">Optional extra instructions appended to the base system prompt.</param>
/// <param name="IsSubAgent">Whether this agent can be invoked via the <c>task</c> tool.</param>
/// <param name="Hidden">When <see langword="true" />, the agent is hidden from <c>/agents</c> listings.</param>
/// <param name="ToolTimeoutSeconds">
///     A9 (sprint 5): per-tool-call execution timeout in seconds. When set, a
///     hanging tool is cancelled at this deadline and returns an error result
///     instead of blocking the loop forever. <see langword="null"/> (default)
///     preserves the legacy unbounded behaviour.
/// </param>
/// <param name="Budget">
///     B2.3 (#404): token/spend/output-size ceilings for runs of this agent.
///     <see langword="null"/> (default) means uncapped — the loop then pays a
///     single null-branch per turn and no budget bookkeeping runs at all.
/// </param>
public sealed record AgentDefinition(
    AgentName Name,
    string DisplayName,
    string Description,
    string Model,
    string ProviderId,
    PermissionRuleset Permission,
    int MaxSteps = 50,
    decimal? Temperature = null,
    ReasoningEffort? ReasoningEffort = null,
    string? SystemPromptAppend = null,
    bool IsSubAgent = false,
    bool Hidden = false,
    int? ToolTimeoutSeconds = null,
    RunBudgetCaps? Budget = null)
{
    /// <summary>
    ///     Returns the default <c>code</c> agent — full read/write/edit/bash permissions, 50 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition" /> for coding tasks.</returns>
    public static AgentDefinition CodeDefault(string model, string providerId) => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent. Can read, write, edit files and run commands.",
        model,
        providerId,
        PermissionRuleset.Default);

    /// <summary>
    ///     Returns the default <c>plan</c> agent — read-only exploration plus git/cat, 100 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition" /> that cannot modify files.</returns>
    /// <remarks>
    ///     <para>
    ///         The bash allow-rules are a SUBSET of <see cref="PermissionRuleset.Default" />'s, and
    ///         that is the invariant rather than a preference: an agent whose description says it
    ///         cannot modify files must not reach, unprompted, a shell command the full-access
    ///         <c>code</c> agent is itself only allowed after narrower enumeration.
    ///     </para>
    ///     <para>
    ///         The git row used to be <c>new("bash", "git *", Allow)</c>, and it was the hole
    ///         #798 did not report. A trailing <c>*</c> in a rule pattern is OPEN-ENDED, so
    ///         <c>git *</c> matches <c>git push --force</c>: that single rule authorized 16 of 16
    ///         mutating git subcommands (<c>commit</c>, <c>push</c>, <c>reset --hard</c>,
    ///         <c>checkout .</c>, <c>clean -fdx</c>, <c>rebase</c>, <c>apply</c>) at
    ///         <see cref="PermissionAction.Allow" />, with no prompt, in an agent that documents
    ///         itself as unable to modify files. <c>Default</c> allows three read-only git verbs;
    ///         the read-only agent was allowing all of git, and <c>ExploreDefault</c>, its
    ///         sibling, allowed none.
    ///     </para>
    ///     <para>
    ///         The tool LIST was not the defence either. <c>IToolRegistry.ResolveTools</c> hides
    ///         <c>bash</c> from this agent because its catch-all is <c>Deny</c>, but
    ///         <c>ToolDispatcher</c> resolves a called tool against the FULL registry and gates
    ///         only on the ruleset. The tool list shapes the prompt; the ruleset is the boundary,
    ///         and the ruleset said yes.
    ///     </para>
    ///     <para>
    ///         <c>ReadOnlyAgentShellAllowRule</c> (tests/Harbor.Architecture.Tests) grades that
    ///         subset relation against <c>Default</c> itself, so a fourth read-only agent or a
    ///         sixth shell verb is covered without a written roster.
    ///     </para>
    /// </remarks>
    public static AgentDefinition PlanDefault(string model, string providerId) => new(
        AgentName.Create("plan"),
        "Plan",
        "Read-only planning agent. Cannot modify files.",
        model,
        providerId,
        new PermissionRuleset(new PermissionRule[]
        {
            new(WellKnownTools.Read, "*", PermissionAction.Allow),
            new(WellKnownTools.Glob, "*", PermissionAction.Allow),
            new(WellKnownTools.Grep, "*", PermissionAction.Allow),
            new(WellKnownTools.Ls, "*", PermissionAction.Allow),
            new(WellKnownTools.Bash, "ls *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "cat *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git status", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git diff *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git log *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "*", PermissionAction.Deny),
            new(WellKnownTools.Edit, "*", PermissionAction.Deny),
            new(WellKnownTools.Write, "*", PermissionAction.Deny),
            // #1738: the agent-facing plugin management tool is REFUSED to the
            // read-only agents, exactly as write / edit / bash are. The tool
            // asks the user about every change it proposes, so allowing it
            // here would hand a design-only agent a way to start a change;
            // denying it keeps the posture these rulesets state.
            new(WellKnownTools.PluginManager, "*", PermissionAction.Deny)
        }),
        100,
        SystemPromptAppend: "You are in PLAN mode. You can read and explore but cannot modify files. Produce a detailed plan for the user to approve.");

    /// <summary>
    ///     Returns the default <c>explore</c> sub-agent — fast read-only codebase explorer, 20 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use sub-agent <see cref="AgentDefinition" /> invocable via the <c>task</c> tool.</returns>
    public static AgentDefinition ExploreDefault(string model, string providerId) => new(
        AgentName.Create("explore"),
        "Explore",
        "Fast read-only codebase explorer.",
        model,
        providerId,
        new PermissionRuleset(new PermissionRule[]
        {
            new(WellKnownTools.Read, "*", PermissionAction.Allow),
            new(WellKnownTools.Glob, "*", PermissionAction.Allow),
            new(WellKnownTools.Grep, "*", PermissionAction.Allow),
            new(WellKnownTools.Ls, "*", PermissionAction.Allow),
            new(WellKnownTools.Bash, "ls *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "cat *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "find *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "rg *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "*", PermissionAction.Deny),
            new(WellKnownTools.Edit, "*", PermissionAction.Deny),
            new(WellKnownTools.Write, "*", PermissionAction.Deny),
            // #1738: the agent-facing plugin management tool is REFUSED to the
            // read-only agents, exactly as write / edit / bash are. The tool
            // asks the user about every change it proposes, so allowing it
            // here would hand a design-only agent a way to start a change;
            // denying it keeps the posture these rulesets state.
            new(WellKnownTools.PluginManager, "*", PermissionAction.Deny),
            new(WellKnownTools.WebFetch, "*", PermissionAction.Allow)
        }),
        20,
        IsSubAgent: true,
        SystemPromptAppend: "You are a fast codebase explorer. Quickly gather information and report back concisely.");

    /// <summary>
    ///     Returns the default <c>ask</c> mode — read-only question-answering, 50 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition" /> that answers and explains without modifying files.</returns>
    /// <remarks>
    ///     <para>
    ///         PX5 slice 1 (issue #1251): modes ARE agents — the registry is the
    ///         declaration, so a mode is a factory plus a registration and nothing
    ///         else (#582). The three mode axes live on this record already:
    ///         <see cref="Permission" /> is the permission preset,
    ///         <see cref="SystemPromptAppend" /> is the prompt bias, and the tool
    ///         allowlist is derived — <c>IToolRegistry.ResolveTools</c> offers a
    ///         tool exactly when the ruleset Allows it, so no hand-written tool
    ///         list exists to rot (#595).
    ///     </para>
    ///     <para>
    ///         The bash allow-verbs are a SUBSET of <see cref="PermissionRuleset.Default" />'s,
    ///         and the write-class tools (<c>write</c> category: write/edit/patch/
    ///         notebook/session_steer) are Denied — the same two invariants
    ///         <c>PlanDefault</c> holds, graded by <c>ReadOnlyAgentShellAllowRule</c>
    ///         and <c>ReadOnlyAgentPermissionRule</c>.
    ///     </para>
    /// </remarks>
    public static AgentDefinition AskDefault(string model, string providerId) => new(
        AgentName.Create("ask"),
        "Ask",
        "Read-only question-answering agent. Cannot modify files.",
        model,
        providerId,
        new PermissionRuleset(new PermissionRule[]
        {
            new(WellKnownTools.Read, "*", PermissionAction.Allow),
            new(WellKnownTools.Glob, "*", PermissionAction.Allow),
            new(WellKnownTools.Grep, "*", PermissionAction.Allow),
            new(WellKnownTools.Ls, "*", PermissionAction.Allow),
            new(WellKnownTools.Tree, "*", PermissionAction.Allow),
            new(WellKnownTools.RipGrep, "*", PermissionAction.Allow),
            new(WellKnownTools.Lsp, "*", PermissionAction.Allow),
            new(WellKnownTools.Skill, "*", PermissionAction.Allow),
            new(WellKnownTools.SessionRead, "*", PermissionAction.Allow),
            new(WellKnownTools.WebFetch, "*", PermissionAction.Allow),
            new(WellKnownTools.Bash, "ls *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "cat *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "grep *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "rg *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "find *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git status", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git diff *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git log *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "*", PermissionAction.Deny),
            new(WellKnownTools.Edit, "*", PermissionAction.Deny),
            new(WellKnownTools.Write, "*", PermissionAction.Deny),
            // #1738: the agent-facing plugin management tool is REFUSED to the
            // read-only agents, exactly as write / edit / bash are. The tool
            // asks the user about every change it proposes, so allowing it
            // here would hand a design-only agent a way to start a change;
            // denying it keeps the posture these rulesets state.
            new(WellKnownTools.PluginManager, "*", PermissionAction.Deny)
        }),
        50,
        SystemPromptAppend: "You are in ASK mode. Answer questions and explain code. Do not implement changes, do not modify files. When asked to change something, explain how instead.");

    /// <summary>
    ///     Returns the default <c>architect</c> mode — read-only software design, 100 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition" /> that designs and plans without modifying files.</returns>
    /// <remarks>
    ///     Same read-only invariants as <see cref="AskDefault" /> (subset shell
    ///     verbs, denied write category), plus the <c>task</c> tool so the design
    ///     can fan exploration out to read-only sub-agents.
    /// </remarks>
    public static AgentDefinition ArchitectDefault(string model, string providerId) => new(
        AgentName.Create("architect"),
        "Architect",
        "Read-only software architect. Cannot modify files — produces designs for approval.",
        model,
        providerId,
        new PermissionRuleset(new PermissionRule[]
        {
            new(WellKnownTools.Read, "*", PermissionAction.Allow),
            new(WellKnownTools.Glob, "*", PermissionAction.Allow),
            new(WellKnownTools.Grep, "*", PermissionAction.Allow),
            new(WellKnownTools.Ls, "*", PermissionAction.Allow),
            new(WellKnownTools.Tree, "*", PermissionAction.Allow),
            new(WellKnownTools.RipGrep, "*", PermissionAction.Allow),
            new(WellKnownTools.Lsp, "*", PermissionAction.Allow),
            new(WellKnownTools.Skill, "*", PermissionAction.Allow),
            new(WellKnownTools.SessionRead, "*", PermissionAction.Allow),
            new(WellKnownTools.WebFetch, "*", PermissionAction.Allow),
            new(WellKnownTools.Task, "*", PermissionAction.Allow),
            new(WellKnownTools.Bash, "ls *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "cat *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "grep *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "rg *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "find *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git status", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git diff *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git log *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "*", PermissionAction.Deny),
            new(WellKnownTools.Edit, "*", PermissionAction.Deny),
            new(WellKnownTools.Write, "*", PermissionAction.Deny),
            // #1738: the agent-facing plugin management tool is REFUSED to the
            // read-only agents, exactly as write / edit / bash are. The tool
            // asks the user about every change it proposes, so allowing it
            // here would hand a design-only agent a way to start a change;
            // denying it keeps the posture these rulesets state.
            new(WellKnownTools.PluginManager, "*", PermissionAction.Deny)
        }),
        100,
        SystemPromptAppend: "You are in ARCHITECT mode. Design before code: explore the codebase, weigh trade-offs, and produce a concrete implementation plan for approval. Do not modify files.");

    /// <summary>
    ///     Returns the default <c>debug</c> mode — failure diagnosis with approval-gated fixes, 50 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition" /> that reproduces, isolates, and fixes failures.</returns>
    /// <remarks>
    ///     NOT a read-only agent: it runs test/build commands and applies fixes,
    ///     so the description deliberately avoids the read-only declaration the
    ///     shell/tool guards derive from. Writes and unlisted shell stay
    ///     <see cref="PermissionAction.Ask" /> — the prompt is the policy.
    /// </remarks>
    public static AgentDefinition DebugDefault(string model, string providerId) => new(
        AgentName.Create("debug"),
        "Debug",
        "Diagnostic agent. Reproduces failures, isolates causes, and fixes with approval.",
        model,
        providerId,
        new PermissionRuleset(new PermissionRule[]
        {
            new(WellKnownTools.Read, "*", PermissionAction.Allow),
            // #1761: the goal axis. The agent's verdict on the session's own
            // goal is a report, not a workspace mutation, so it needs no
            // approval.
            new("goal", "*", PermissionAction.Allow),
            new(WellKnownTools.Glob, "*", PermissionAction.Allow),
            new(WellKnownTools.Grep, "*", PermissionAction.Allow),
            new(WellKnownTools.Ls, "*", PermissionAction.Allow),
            new(WellKnownTools.Tree, "*", PermissionAction.Allow),
            new(WellKnownTools.RipGrep, "*", PermissionAction.Allow),
            new(WellKnownTools.Lsp, "*", PermissionAction.Allow),
            new(WellKnownTools.Skill, "*", PermissionAction.Allow),
            new(WellKnownTools.SessionRead, "*", PermissionAction.Allow),
            new(WellKnownTools.WebFetch, "*", PermissionAction.Allow),
            new(WellKnownTools.Bash, "ls *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "cat *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "grep *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "rg *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "find *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git status", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git diff *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git log *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "dotnet test *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "dotnet build *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "npm test *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "npm run build *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "rm -rf /", PermissionAction.Deny),
            new(WellKnownTools.Bash, "sudo *", PermissionAction.Deny),
            new(WellKnownTools.Bash, "*", PermissionAction.Ask),
            new(WellKnownTools.Edit, "*", PermissionAction.Ask),
            new(WellKnownTools.Write, "*", PermissionAction.Ask),
            // #1738: the agent-facing plugin management tool. Allow does not
            // mean "without asking": the tool asks the user about every
            // change it proposes, and the answer covers that call only — the
            // same posture as this agent's write / edit rules.
            new(WellKnownTools.PluginManager, "*", PermissionAction.Allow)
        }),
        50,
        SystemPromptAppend: "You are in DEBUG mode. Diagnose first: reproduce the failure, isolate the cause, then fix. Run tests to confirm. Broad or destructive changes need explicit user approval.");

    /// <summary>
    ///     Returns the default <c>orchestrator</c> mode — work decomposition and delegation, 100 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition" /> that coordinates sub-agents instead of implementing directly.</returns>
    /// <remarks>
    ///     Direct writes are Denied — the orchestrator implements nothing itself —
    ///     but it is not a read-only agent either: it fans work out through
    ///     <c>task</c> (Allowed) while live-session steering stays Denied
    ///     (<c>session_steer</c> declares <c>ToolCategory.Write</c>, so the
    ///     write-category Deny shadows any narrower Ask on tie-sort), and the
    ///     description avoids the read-only declaration accordingly.
    /// </remarks>
    public static AgentDefinition OrchestratorDefault(string model, string providerId) => new(
        AgentName.Create("orchestrator"),
        "Orchestrator",
        "Coordination agent. Decomposes work and delegates through the task tool.",
        model,
        providerId,
        new PermissionRuleset(new PermissionRule[]
        {
            new(WellKnownTools.Read, "*", PermissionAction.Allow),
            // #1761: the goal axis. The agent's verdict on the session's own
            // goal is a report, not a workspace mutation, so it needs no
            // approval — and the orchestrator is the agent that most needs to
            // be able to say "the legs are done".
            new("goal", "*", PermissionAction.Allow),
            new(WellKnownTools.Glob, "*", PermissionAction.Allow),
            new(WellKnownTools.Grep, "*", PermissionAction.Allow),
            new(WellKnownTools.Ls, "*", PermissionAction.Allow),
            new(WellKnownTools.Tree, "*", PermissionAction.Allow),
            new(WellKnownTools.Skill, "*", PermissionAction.Allow),
            new(WellKnownTools.SessionRead, "*", PermissionAction.Allow),
            new(WellKnownTools.SessionSteer, "*", PermissionAction.Deny),
            new(WellKnownTools.Task, "*", PermissionAction.Allow),
            new(WellKnownTools.Bash, "ls *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "cat *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git status", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git diff *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "git log *", PermissionAction.Allow),
            new(WellKnownTools.Bash, "*", PermissionAction.Deny),
            new(WellKnownTools.Edit, "*", PermissionAction.Deny),
            new(WellKnownTools.Write, "*", PermissionAction.Deny),
            // #1738: the agent-facing plugin management tool is REFUSED to the
            // read-only agents, exactly as write / edit / bash are. The tool
            // asks the user about every change it proposes, so allowing it
            // here would hand a design-only agent a way to start a change;
            // denying it keeps the posture these rulesets state.
            new(WellKnownTools.PluginManager, "*", PermissionAction.Deny)
        }),
        100,
        SystemPromptAppend: "You are in ORCHESTRATOR mode. Decompose the goal and delegate through the task tool; do not implement directly. Track each leg to done.");

    /// <summary>
    ///     Returns a copy of this agent bound to a different model and provider.
    /// </summary>
    /// <param name="model">New model id.</param>
    /// <param name="providerId">New provider id.</param>
    /// <returns>A new <see cref="AgentDefinition" /> instance.</returns>
    public AgentDefinition WithModel(string model, string providerId) => this with
    {
        Model = model,
        ProviderId = providerId
    };

    /// <summary>
    ///     Returns a copy of this agent with a different permission ruleset.
    /// </summary>
    /// <param name="permission">The new ruleset to apply.</param>
    /// <returns>A new <see cref="AgentDefinition" /> instance.</returns>
    public AgentDefinition WithPermission(PermissionRuleset permission) => this with
    {
        Permission = permission
    };
}

/// <summary>
///     Registry of agents. Implements the Registry pattern (GOF).
/// </summary>
/// <remarks>
///     <para>
///         The registry is the canonical lookup table for <see cref="AgentDefinition" /> instances.
///         Builtin agents (<c>code</c>, <c>plan</c>, <c>explore</c> and the PX5
///         modes <c>ask</c>, <c>architect</c>, <c>debug</c>, <c>orchestrator</c>)
///         are registered at startup;
///         plugins add additional agents via <see cref="IAgentRegistryBuilder" /> during
///         initialization.
///     </para>
///     <para>
///         Implementations MUST be thread-safe. The default <c>AgentRegistry</c> uses a
///         <c>NonBlocking.ConcurrentDictionary</c> for lock-free scaling and is safe for
///         concurrent reads and writes.
///     </para>
/// </remarks>
public interface IAgentRegistry
{
    /// <summary>
    ///     Returns a snapshot of all registered agents.
    /// </summary>
    /// <returns>A read-only list of <see cref="AgentDefinition" /> instances.</returns>
    public IReadOnlyList<AgentDefinition> GetAllAgents();

    /// <summary>
    ///     Look up an agent by name.
    /// </summary>
    /// <param name="name">The agent's stable identifier.</param>
    /// <returns>Success with the agent, or failure if not registered.</returns>
    public Result<AgentDefinition> GetAgent(AgentName name);

    /// <summary>
    ///     Register a new agent. Fails if an agent with the same name is already registered.
    /// </summary>
    /// <param name="agent">The agent definition to register.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Result Register(AgentDefinition agent);

    /// <summary>
    ///     Unregister an agent by name.
    /// </summary>
    /// <param name="name">The agent's stable identifier.</param>
    /// <returns>Success, or failure if the agent is not registered.</returns>
    public Result Unregister(AgentName name);
}

/// <summary>
///     Builder for agent registration (used by plugins).
/// </summary>
/// <remarks>
///     Plugins receive an <see cref="IAgentRegistryBuilder" /> during initialization and call
///     <see cref="AddAgent" /> for each agent they wish to contribute. The builder converts
///     registration failures into <see cref="InvalidOperationException" /> so plugin authors
///     get fail-fast behavior instead of silently dropped registrations.
/// </remarks>
public interface IAgentRegistryBuilder
{
    /// <summary>
    ///     Register an agent. Throws if an agent with the same name is already registered.
    /// </summary>
    /// <param name="agent">The agent definition to register.</param>
    public void AddAgent(AgentDefinition agent);
}
