using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;

namespace Harbor.Abstractions.Agents;

/// <summary>
/// Agent definition. An "agent" combines a model, a permission ruleset, and a max-steps limit.
/// Implements Prototype pattern (GOF) — agents are cloned and customized.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="AgentDefinition"/> is the static blueprint for an agent run. It is paired
/// with a <see cref="Session"/> at runtime via <see cref="IAgent.Initialize"/>. Agents are
/// immutable records: use the <see langword="with"/> expression or the <c>WithModel</c> /
/// <c>WithPermission</c> helpers to derive customized variants.
/// </para>
/// <para>
/// Sub-agents (those with <see cref="IsSubAgent"/> = <see langword="true"/>) are invocable
/// from the <c>task</c> tool for delegated work. They run in their own context with their
/// own permission ruleset and a typically tighter step budget.
/// </para>
/// <para>
/// Thread-safety: instances are immutable and safe to share across threads.
/// </para>
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
/// <param name="Hidden">When <see langword="true"/>, the agent is hidden from <c>/agents</c> listings.</param>
public sealed record AgentDefinition(
    AgentName Name,
    string DisplayName,
    string Description,
    string Model,
    string ProviderId,
    Permissions.PermissionRuleset Permission,
    int MaxSteps = 50,
    decimal? Temperature = null,
    ReasoningEffort? ReasoningEffort = null,
    string? SystemPromptAppend = null,
    bool IsSubAgent = false,
    bool Hidden = false)
{
    /// <summary>
    /// Returns the default <c>code</c> agent — full read/write/edit/bash permissions, 50 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition"/> for coding tasks.</returns>
    public static AgentDefinition CodeDefault(string model, string providerId) => new(
        Name: AgentName.Create("code"),
        DisplayName: "Code",
        Description: "Default coding agent. Can read, write, edit files and run commands.",
        Model: model,
        ProviderId: providerId,
        Permission: Permissions.PermissionRuleset.Default,
        MaxSteps: 50);

    /// <summary>
    /// Returns the default <c>plan</c> agent — read-only exploration plus git/cat, 100 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use <see cref="AgentDefinition"/> that cannot modify files.</returns>
    public static AgentDefinition PlanDefault(string model, string providerId) => new(
        Name: AgentName.Create("plan"),
        DisplayName: "Plan",
        Description: "Read-only planning agent. Cannot modify files.",
        Model: model,
        ProviderId: providerId,
        Permission: new Permissions.PermissionRuleset(new Permissions.PermissionRule[]
        {
            new("read", "*", Permissions.PermissionAction.Allow),
            new("glob", "*", Permissions.PermissionAction.Allow),
            new("grep", "*", Permissions.PermissionAction.Allow),
            new("ls", "*", Permissions.PermissionAction.Allow),
            new("bash", "ls *", Permissions.PermissionAction.Allow),
            new("bash", "cat *", Permissions.PermissionAction.Allow),
            new("bash", "git *", Permissions.PermissionAction.Allow),
            new("bash", "*", Permissions.PermissionAction.Deny),
            new("edit", "*", Permissions.PermissionAction.Deny),
            new("write", "*", Permissions.PermissionAction.Deny),
        }),
        MaxSteps: 100,
        SystemPromptAppend: "You are in PLAN mode. You can read and explore but cannot modify files. Produce a detailed plan for the user to approve.");

    /// <summary>
    /// Returns the default <c>explore</c> sub-agent — fast read-only codebase explorer, 20 steps.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="providerId">Provider id (e.g. <c>anthropic</c>).</param>
    /// <returns>A ready-to-use sub-agent <see cref="AgentDefinition"/> invocable via the <c>task</c> tool.</returns>
    public static AgentDefinition ExploreDefault(string model, string providerId) => new(
        Name: AgentName.Create("explore"),
        DisplayName: "Explore",
        Description: "Fast read-only codebase explorer.",
        Model: model,
        ProviderId: providerId,
        Permission: new Permissions.PermissionRuleset(new Permissions.PermissionRule[]
        {
            new("read", "*", Permissions.PermissionAction.Allow),
            new("glob", "*", Permissions.PermissionAction.Allow),
            new("grep", "*", Permissions.PermissionAction.Allow),
            new("ls", "*", Permissions.PermissionAction.Allow),
            new("bash", "ls *", Permissions.PermissionAction.Allow),
            new("bash", "cat *", Permissions.PermissionAction.Allow),
            new("bash", "find *", Permissions.PermissionAction.Allow),
            new("bash", "rg *", Permissions.PermissionAction.Allow),
            new("bash", "*", Permissions.PermissionAction.Deny),
            new("edit", "*", Permissions.PermissionAction.Deny),
            new("write", "*", Permissions.PermissionAction.Deny),
            new("webfetch", "*", Permissions.PermissionAction.Allow),
        }),
        MaxSteps: 20,
        IsSubAgent: true,
        SystemPromptAppend: "You are a fast codebase explorer. Quickly gather information and report back concisely.");

    /// <summary>
    /// Returns a copy of this agent bound to a different model and provider.
    /// </summary>
    /// <param name="model">New model id.</param>
    /// <param name="providerId">New provider id.</param>
    /// <returns>A new <see cref="AgentDefinition"/> instance.</returns>
    public AgentDefinition WithModel(string model, string providerId) => this with
    {
        Model = model,
        ProviderId = providerId,
    };

    /// <summary>
    /// Returns a copy of this agent with a different permission ruleset.
    /// </summary>
    /// <param name="permission">The new ruleset to apply.</param>
    /// <returns>A new <see cref="AgentDefinition"/> instance.</returns>
    public AgentDefinition WithPermission(Permissions.PermissionRuleset permission) => this with
    {
        Permission = permission,
    };
}

/// <summary>
/// Registry of agents. Implements the Registry pattern (GOF).
/// </summary>
/// <remarks>
/// <para>
/// The registry is the canonical lookup table for <see cref="AgentDefinition"/> instances.
/// Builtin agents (<c>code</c>, <c>plan</c>, <c>explore</c>) are registered at startup;
/// plugins add additional agents via <see cref="IAgentRegistryBuilder"/> during
/// initialization.
/// </para>
/// <para>
/// Implementations MUST be thread-safe. The default <c>AgentRegistry</c> uses a
/// <c>NonBlocking.ConcurrentDictionary</c> for lock-free scaling and is safe for
/// concurrent reads and writes.
/// </para>
/// </remarks>
public interface IAgentRegistry
{
    /// <summary>
    /// Returns a snapshot of all registered agents.
    /// </summary>
    /// <returns>A read-only list of <see cref="AgentDefinition"/> instances.</returns>
    IReadOnlyList<AgentDefinition> GetAllAgents();

    /// <summary>
    /// Look up an agent by name.
    /// </summary>
    /// <param name="name">The agent's stable identifier.</param>
    /// <returns>Success with the agent, or failure if not registered.</returns>
    Result<AgentDefinition> GetAgent(AgentName name);

    /// <summary>
    /// Register a new agent. Fails if an agent with the same name is already registered.
    /// </summary>
    /// <param name="agent">The agent definition to register.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Result Register(AgentDefinition agent);

    /// <summary>
    /// Unregister an agent by name.
    /// </summary>
    /// <param name="name">The agent's stable identifier.</param>
    /// <returns>Success, or failure if the agent is not registered.</returns>
    Result Unregister(AgentName name);
}

/// <summary>
/// Builder for agent registration (used by plugins).
/// </summary>
/// <remarks>
/// Plugins receive an <see cref="IAgentRegistryBuilder"/> during initialization and call
/// <see cref="AddAgent"/> for each agent they wish to contribute. The builder converts
/// registration failures into <see cref="InvalidOperationException"/> so plugin authors
/// get fail-fast behavior instead of silently dropped registrations.
/// </remarks>
public interface IAgentRegistryBuilder
{
    /// <summary>
    /// Register an agent. Throws if an agent with the same name is already registered.
    /// </summary>
    /// <param name="agent">The agent definition to register.</param>
    void AddAgent(AgentDefinition agent);
}
