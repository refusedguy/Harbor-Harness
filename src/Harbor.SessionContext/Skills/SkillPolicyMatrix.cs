using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Skills;

/// <summary>
///     One row of the skill invocation matrix: the skills allowed when the
///     request matches the row's axes.
/// </summary>
/// <remarks>
///     <para>
///         A row is DATA, not code: the axes it names and the skills it
///         admits. Three axes, each optional, and a null axis matches
///         anything:
///     </para>
///     <list type="bullet">
///         <item><c>Source</c> — who is asking (the model or the human).</item>
///         <item><c>Mode</c> — the mode the call runs in (the agent's name).</item>
///         <item><c>Category</c> — the category of task.</item>
///     </list>
/// </remarks>
public sealed record SkillPolicyRule
{
    /// <summary>Build a row. A null skill list admits nothing.</summary>
    public SkillPolicyRule(
        SkillCaller? source = null,
        string? mode = null,
        string? category = null,
        IReadOnlyCollection<string>? allowedSkills = null)
    {
        Source = source;
        Mode = mode;
        Category = category;
        Skills = allowedSkills ?? Array.Empty<string>();
    }

    /// <summary>The caller this row is about, or null for any caller.</summary>
    public SkillCaller? Source { get; init; }

    /// <summary>The mode this row is about, or null for any mode.</summary>
    public string? Mode { get; init; }

    /// <summary>The task category this row is about, or null for any category.</summary>
    public string? Category { get; init; }

    /// <summary>The skill names this row admits. Never null; an absent list admits nothing.</summary>
    public IReadOnlyCollection<string> Skills { get; init; }

    /// <summary>
    ///     Whether this row's axes match the request. Every axis the row
    ///     names must match; an axis it leaves null matches anything.
    /// </summary>
    public bool Matches(SkillCaller caller, string? mode, string? category)
    {
        if (Source is { } source && source != caller)
        {
            return false;
        }

        if (Mode is { } rowMode && !string.Equals(rowMode, mode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Category is { } rowCategory
            && !string.Equals(rowCategory, category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Whether this row admits the named skill.</summary>
    public bool Allows(string skillName)
    {
        foreach (string name in Skills)
        {
            if (string.Equals(name, skillName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     The skill invocation matrix: the data half of "who may call this
///     skill, and when".
/// </summary>
/// <remarks>
///     <para>
///         The matrix NARROWS. It never widens: a skill the provider declared
///         as model-only stays model-only whatever a row says, because the
///         row list is a permission, not a grant.
///     </para>
///     <para>
///         The narrowing is per REQUEST, and a request no row matches is not
///         narrowed at all — the skill's own policy is the whole answer there.
///         A request some row matches is answered by the matching rows: the
///         skill is allowed if and only if one of them names it. That makes
///         "no policy for this context" and "a policy for this context" two
///         different, predictable answers rather than one silent default.
///     </para>
///     <para>
///         The product ships an empty matrix today (see
///         <c>Harbor.Hosting/Modules/RegistryFacade.cs</c>): the per-skill
///         declaration in the skill file's front-matter is what narrows a
///         skill today, and the matrix is the layer that narrows by request
///         context once a host has rows to give it.
///     </para>
/// </remarks>
public sealed class SkillPolicyMatrix
{
    /// <summary>The matrix that narrows nothing: every request is answered by the skill's own policy.</summary>
    public static SkillPolicyMatrix Empty { get; } = new([]);

    private readonly SkillPolicyRule[] _rules;

    /// <summary>Build a matrix from its rows.</summary>
    public SkillPolicyMatrix(IReadOnlyList<SkillPolicyRule> rules)
        => _rules = [.. rules];

    /// <summary>The matrix's rows, in declaration order.</summary>
    public IReadOnlyList<SkillPolicyRule> Rules => _rules;

    /// <summary>
    ///     Whether the named skill is allowed for the request. A request no
    ///     row matches is not narrowed: the answer is true, and the skill's
    ///     own policy decides.
    /// </summary>
    public bool Allows(string skillName, SkillCaller caller, string? mode, string? category)
    {
        bool narrowed = false;

        foreach (SkillPolicyRule rule in _rules)
        {
            if (!rule.Matches(caller, mode, category))
            {
                continue;
            }

            narrowed = true;

            if (rule.Allows(skillName))
            {
                return true;
            }
        }

        return !narrowed;
    }
}
