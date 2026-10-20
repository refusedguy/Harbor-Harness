using Harbor.Abstractions.Agents;

namespace Harbor.Application.Configuration;

/// <summary>
///     The step budget a configuration asks for, projected onto an agent
///     definition (#1118).
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect this closes.</b> <c>maxSteps</c> in the home config
///         (<c>~/.harbor/config.json</c>) was persisted, printed by
///         <c>harbor config</c> and settable through <c>/config maxsteps</c> —
///         and it reached no agent at all. The agents the product runs are
///         declared by the <c>AgentDefinition.*Default</c> factories (the
///         composition root's <c>ToolsCatalog.CreateAgentRegistry</c>), each
///         carrying its own <see cref="AgentDefinition.MaxSteps" />, and the
///         configured value was never projected onto them. The REPL therefore
///         ran to the AGENT's default (50 turns for <c>code</c>) whatever the
///         config said: the PTY scenarios of #1057 seeded
///         <c>"maxSteps": 1</c> into the home config and still watched the
///         same tool call execute dozens of times.
///     </para>
///     <para>
///         <b>Why there is no second limit axis.</b> The loop already enforces
///         the budget — <c>MaxStepsBehavior.IsExhausted</c> compares the turn
///         counter against <see cref="AgentDefinition.MaxSteps" /> — so the
///         missing piece was never a new mechanism but the ONE value that
///         reaches that comparison. It is a configuration concern, so it is
///         projected where the configuration is read, and it lands on the
///         existing axis.
///     </para>
/// </remarks>
public static class AgentStepBudget
{
    /// <summary>
    ///     The step budget <paramref name="config" /> asks for, or <c>null</c>
    ///     when it asks for none.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>The requested budget, or <c>null</c> to keep every agent's own budget.</returns>
    /// <remarks>
    ///     <para>
    ///         The built-in default reads as "not configured" ON PURPOSE. 50 is
    ///         the value every config save writes into config.json
    ///         (<see cref="HarborConfig.ToRaw" />), so a config that carries it
    ///         says nothing the agent's own budget does not — and honouring it
    ///         as an override would silently halve the 100-step agents
    ///         (<c>plan</c>, <c>architect</c>, <c>orchestrator</c>) for every
    ///         user who never touched the key. A value that DIFFERS from the
    ///         default is a statement the user made, and it is the budget of the
    ///         run.
    ///     </para>
    ///     <para>
    ///         The consequence is stated here rather than discovered later: a
    ///         configured value of exactly 50 is the one number that cannot be
    ///         told apart from the default, so it leaves the agents' own
    ///         budgets in place. Every other value is honoured.
    ///     </para>
    /// </remarks>
    public static int? Configured(HarborConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        int maxSteps = config.MaxSteps;
        return maxSteps == RunLimitsConfig.Default.MaxSteps ? null : maxSteps;
    }

    /// <summary>
    ///     Returns <paramref name="definition" /> carrying the configured step
    ///     budget — the definition unchanged when the configuration asks for
    ///     none.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="definition">The agent definition the budget applies to.</param>
    /// <returns>The definition with the configured budget applied.</returns>
    public static AgentDefinition Apply(HarborConfig config, AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Configured(config) is { } steps ? definition with { MaxSteps = steps } : definition;
    }
}
