namespace Harbor.Abstractions.Sessions;

/// <summary>
///     Which surface is asking for a skill.
/// </summary>
/// <remarks>
///     The two callers are the two catalogs the product keeps apart: the
///     model-facing one (the <c>&lt;available_skills&gt;</c> block and the
///     <c>skill</c> tool) and the human-facing one (a slash command, a picker).
///     A skill can be useful to one and noise to the other, so the caller is
///     part of the question "may this be invoked" rather than a detail of it.
/// </remarks>
public enum SkillCaller
{
    /// <summary>The model, through a tool the LLM can call.</summary>
    Model = 0,

    /// <summary>The human, through a command or a picker.</summary>
    User = 1,
}

/// <summary>
///     The registry that owns skill discovery: it merges the catalogs of every
///     registered <see cref="ISkillProvider" />, deduplicates them by name under
///     one declared rule, and applies the invocation policy.
/// </summary>
/// <remarks>
///     <para>
///         A skill is a NAME, a DESCRIPTION and a policy — "when may this be
///         called". Where the body comes from is the provider's business; what
///         the registry guarantees is that ONE answer serves both surfaces, so
///         the model-facing catalog and the model-facing call cannot disagree
///         about which skills exist.
///     </para>
///     <para>
///         Implementations MUST be thread-safe.
///     </para>
/// </remarks>
public interface ISkillRegistry
{
    /// <summary>
    ///     The skills the given caller may see: merged across every registered
    ///     provider, deduplicated by name, and filtered by the invocation
    ///     policy.
    /// </summary>
    /// <param name="workingDirectory">The session's working directory; project-local skills live under it.</param>
    /// <param name="caller">Which surface is asking.</param>
    /// <param name="mode">The mode the call runs in (the agent's name), or null for "any mode".</param>
    /// <param name="category">The task category, or null for "any category".</param>
    IReadOnlyList<SkillDescriptor> GetSkills(
        string workingDirectory,
        SkillCaller caller,
        string? mode = null,
        string? category = null);

    /// <summary>
    ///     Resolve one skill for a call, under the same policy as
    ///     <see cref="GetSkills" />.
    /// </summary>
    /// <param name="workingDirectory">The session's working directory.</param>
    /// <param name="name">The skill name, as the caller spells it.</param>
    /// <param name="caller">Which surface is asking.</param>
    /// <param name="mode">The mode the call runs in, or null for "any mode".</param>
    /// <param name="category">The task category, or null for "any category".</param>
    /// <returns>
    ///     The skill, or a failure whose message says WHY the call is refused:
    ///     an unknown name, or a policy that does not admit this caller.
    /// </returns>
    Result<SkillDescriptor> ResolveForCall(
        string workingDirectory,
        string name,
        SkillCaller caller,
        string? mode = null,
        string? category = null);
}
