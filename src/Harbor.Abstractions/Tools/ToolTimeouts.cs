namespace Harbor.Abstractions.Tools;

/// <summary>
///     The per-tool execution budget and how it combines with the common
///     default (#1760).
/// </summary>
/// <remarks>
///     <para>
///         A tool call used to have exactly ONE deadline, and it lived on the
///         agent (<see cref="Agents.AgentDefinition.ToolTimeoutSeconds" />).
///         Every tool in a turn therefore shared one budget: a slow
///         <c>webfetch</c> was cut off by the same number of seconds a
///         <c>ls</c> never needed, and a slow <c>webfetch</c> could eat the
///         budget of the fast calls queued behind it.
///     </para>
///     <para>
///         A tool may now declare its own budget through
///         <see cref="ITool.TimeoutMs" />. The precedence is:
///     </para>
///     <list type="number">
///         <item>the tool's own <see cref="ITool.TimeoutMs" />, when it declares one;</item>
///         <item>
///             otherwise the agent's common default
///             (<see cref="Agents.AgentDefinition.ToolTimeoutSeconds" />);
///         </item>
///         <item>otherwise no deadline at all (the legacy unbounded behaviour).</item>
///     </list>
///     <para>
///         <b>Why a tool's own budget wins.</b> The agent's number is a
///         default; the tool's number is a fact about the tool ("a network
///         fetch takes seconds, a directory listing takes milliseconds"). A
///         default that overrode the specific value would make the specific
///         value pointless.
///     </para>
/// </remarks>
public static class ToolTimeouts
{
    /// <summary>
    ///     Whether a declared budget is usable.
    /// </summary>
    /// <remarks>
    ///     A missing budget (<see langword="null" />) is valid: it means "no
    ///     opinion", and the common default applies. A non-positive budget is
    ///     NOT: a zero budget would cancel the call before it started, and a
    ///     negative one is a typo. The registration path refuses it (#1760).
    /// </remarks>
    /// <param name="toolTimeoutMs">The declared budget in milliseconds.</param>
    /// <returns><see langword="true" /> when the value is absent or positive.</returns>
    public static bool IsValid(int? toolTimeoutMs) => toolTimeoutMs is null or > 0;

    /// <summary>
    ///     The effective deadline for one call: the tool's own budget when it
    ///     declares one, the common default otherwise.
    /// </summary>
    /// <param name="toolTimeoutMs">The tool's declared budget in milliseconds.</param>
    /// <param name="commonDefault">The common default (the agent's budget), if any.</param>
    /// <returns>The effective deadline, or <see langword="null" /> for no deadline.</returns>
    public static TimeSpan? Resolve(int? toolTimeoutMs, TimeSpan? commonDefault) =>
        IsValid(toolTimeoutMs) && toolTimeoutMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : commonDefault;
}
