using Harbor.Abstractions.Agents;

namespace Harbor.Application.Configuration;

/// <summary>
///     The repeat-guard threshold a configuration asks for, projected onto an
///     agent definition (#1756).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this is a projection and not a second limit.</b> The repeat
///     guard itself lives in the agent runtime, next to the loop that counts
///     the calls; what the configuration owns is the ONE number that decides
///     when it speaks up. That number has to reach the agent definition the
///     run uses — the same lesson #1118 taught about <c>maxSteps</c>: a
///     configured value that reaches no agent is a value the user set for
///     nothing.
/// </para>
/// <para>
///     The threshold is NOT a limit: the guard never blocks a call and never
///     ends a run. It is the count after which the MODEL is told that it is
///     repeating itself, so it can change its mind before the step budget or
///     the token budget runs out.
/// </para>
/// </remarks>
public static class AgentRepeatThreshold
{
    /// <summary>
    ///     The threshold <paramref name="config" /> asks for, or <c>null</c>
    ///     when it asks for none.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>The requested threshold, or <c>null</c> to keep every agent's own.</returns>
    /// <remarks>
    ///     The built-in default reads as "not configured" ON PURPOSE, for the
    ///     reason <see cref="AgentStepBudget.Configured" /> gives: the default
    ///     is the value every agent already carries, so honouring it as an
    ///     override would say nothing the agent's own value does not. A value
    ///     that DIFFERS from the default is a statement the user made, and it
    ///     is the threshold of the run.
    /// </remarks>
    public static int? Configured(HarborConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        int threshold = config.Run.RepeatToolThreshold;
        return threshold == RunLimitsConfig.Default.RepeatToolThreshold ? null : threshold;
    }

    /// <summary>
    ///     Returns <paramref name="definition" /> carrying the configured
    ///     threshold — the definition unchanged when the configuration asks for
    ///     none.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="definition">The agent definition the threshold applies to.</param>
    /// <returns>The definition with the configured threshold applied.</returns>
    public static AgentDefinition Apply(HarborConfig config, AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Configured(config) is { } threshold
            ? definition with { RepeatToolThreshold = threshold }
            : definition;
    }
}
