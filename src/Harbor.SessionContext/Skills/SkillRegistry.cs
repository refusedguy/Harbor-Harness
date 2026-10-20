using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Skills;

/// <summary>
///     The default <see cref="ISkillRegistry" />: it merges the catalogs of
///     every registered <see cref="ISkillProvider" />, deduplicates them by
///     name under one declared rule, and applies the invocation policy.
/// </summary>
/// <remarks>
///     <para>
///         THE MERGE RULE, in one place: providers are ordered by their
///         declared <see cref="ISkillProvider.Priority" /> (higher first) and,
///         at equal priority, by their name (ordinal). The FIRST provider to
///         offer a name keeps it; a later one is shadowed and logged. The
///         order never depends on registration order, so adding a second
///         source cannot change which skill wins on the existing ones.
///     </para>
///     <para>
///         Names are compared case-insensitively, because a skill name is
///         something a human types and the model copies out of a prompt —
///         one identity rule for the merge and for the lookup.
///     </para>
///     <para>
///         The registry answers for EVERY surface: the model-facing catalog
///         and the model-facing call go through the same merge and the same
///         policy, so the two cannot disagree about which skills exist.
///     </para>
/// </remarks>
public sealed class SkillRegistry : ISkillRegistry
{
    private readonly ISkillProvider[] _providers;
    private readonly SkillPolicyMatrix _matrix;
    private readonly ILogger<SkillRegistry>? _logger;

    /// <summary>A skill together with the provider that offered it.</summary>
    private sealed record ProviderSkill(SkillDescriptor Skill, string Provider);

    /// <summary>
    ///     Build a registry over the given providers.
    /// </summary>
    /// <param name="providers">The registered providers; an empty list is a registry with no sources.</param>
    /// <param name="matrix">The invocation matrix; null means the empty matrix.</param>
    /// <param name="logger">Optional logger for the shadowing warning.</param>
    public SkillRegistry(
        IEnumerable<ISkillProvider> providers,
        SkillPolicyMatrix? matrix = null,
        ILogger<SkillRegistry>? logger = null)
    {
        _matrix = matrix ?? SkillPolicyMatrix.Empty;
        _logger = logger;
        _providers = Order(providers);
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDescriptor> GetSkills(
        string workingDirectory,
        SkillCaller caller,
        string? mode = null,
        string? category = null)
    {
        IReadOnlyList<SkillDescriptor> merged = Merged(workingDirectory);
        if (merged.Count == 0)
        {
            return merged;
        }

        var allowed = new List<SkillDescriptor>(merged.Count);
        foreach (SkillDescriptor skill in merged)
        {
            if (Admits(skill, caller, mode, category))
            {
                allowed.Add(skill);
            }
        }

        return allowed;
    }

    /// <inheritdoc />
    public Result<SkillDescriptor> ResolveForCall(
        string workingDirectory,
        string name,
        SkillCaller caller,
        string? mode = null,
        string? category = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure<SkillDescriptor>("Skill name must not be empty.");
        }

        SkillDescriptor? found = null;
        foreach (SkillDescriptor skill in Merged(workingDirectory))
        {
            if (string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                found = skill;
                break;
            }
        }

        if (found is null)
        {
            return Result.Failure<SkillDescriptor>(
                $"Skill '{name}' is not available. Skills are listed in the system prompt's <available_skills> block.");
        }

        bool callerAdmitted = caller switch
        {
            SkillCaller.Model => found.ModelInvocable,
            SkillCaller.User => found.UserInvocable,

            // A value the product does not know: fail closed rather than guess
            // which surface is asking.
            _ => false,
        };

        if (!callerAdmitted)
        {
            return Result.Failure<SkillDescriptor>(
                $"Skill '{name}' is not invocable by the {Describe(caller)}: its invocation policy admits "
                + $"{AdmittedBy(found)} only.");
        }

        if (!_matrix.Allows(found.Name, caller, mode, category))
        {
            return Result.Failure<SkillDescriptor>(
                $"Skill '{name}' is not invocable by the {Describe(caller)}: the skill invocation policy "
                + "excludes it for this request.");
        }

        return Result.Success(found);
    }

    /// <summary>
    ///     The providers in the order the merge rule walks them: priority
    ///     first (higher wins), then the provider name (ordinal), then the
    ///     provider type — so the order is total and never depends on
    ///     registration order.
    /// </summary>
    private static ISkillProvider[] Order(IEnumerable<ISkillProvider> providers)
    {
        var list = new List<ISkillProvider>();
        foreach (ISkillProvider provider in providers)
        {
            if (provider is not null)
            {
                list.Add(provider);
            }
        }

        list.Sort(static (left, right) =>
        {
            int byPriority = right.Priority.CompareTo(left.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.GetType().Name, right.GetType().Name);
        });

        return [.. list];
    }

    /// <summary>
    ///     Every provider's catalog, merged and deduplicated by name, sorted
    ///     by name (ordinal) — the shape the prompt builder consumes without
    ///     re-sorting per turn.
    /// </summary>
    private IReadOnlyList<SkillDescriptor> Merged(string workingDirectory)
    {
        if (_providers.Length == 0)
        {
            return [];
        }

        var byName = new Dictionary<string, ProviderSkill>(StringComparer.OrdinalIgnoreCase);

        foreach (ISkillProvider provider in _providers)
        {
            IReadOnlyList<SkillDescriptor>? skills;
            try
            {
                skills = provider.GetSkills(workingDirectory);
            }
            catch (IOException)
            {
                // A provider whose source is unreadable contributes nothing;
                // the other providers still answer.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (skills is null)
            {
                continue;
            }

            foreach (SkillDescriptor skill in skills)
            {
                if (skill is null || string.IsNullOrEmpty(skill.Name))
                {
                    continue;
                }

                if (byName.TryGetValue(skill.Name, out ProviderSkill? winner))
                {
                    LogShadowed(winner, skill, provider);
                    continue;
                }

                byName[skill.Name] = new ProviderSkill(skill, provider.Name);
            }
        }

        if (byName.Count == 0)
        {
            return [];
        }

        var merged = new List<SkillDescriptor>(byName.Count);
        foreach (ProviderSkill entry in byName.Values)
        {
            merged.Add(entry.Skill);
        }

        merged.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return merged;
    }

    /// <summary>Whether the skill's own policy AND the matrix admit this caller.</summary>
    private bool Admits(SkillDescriptor skill, SkillCaller caller, string? mode, string? category)
    {
        bool byCaller = caller switch
        {
            SkillCaller.Model => skill.ModelInvocable,
            SkillCaller.User => skill.UserInvocable,

            // A value the product does not know: fail closed rather than guess
            // which surface is asking.
            _ => false,
        };

        return byCaller && _matrix.Allows(skill.Name, caller, mode, category);
    }

    private static string Describe(SkillCaller caller) => caller switch
    {
        SkillCaller.Model => "model",
        SkillCaller.User => "user",
        _ => "caller",
    };

    /// <summary>Which surface the skill's own policy admits, in words.</summary>
    private static string AdmittedBy(SkillDescriptor skill)
    {
        if (!skill.ModelInvocable && !skill.UserInvocable)
        {
            return "nobody";
        }

        return skill.ModelInvocable ? "the model" : "the human";
    }

    private void LogShadowed(ProviderSkill winner, SkillDescriptor shadowed, ISkillProvider provider)
    {
        if (_logger is null)
        {
            return;
        }

        // The placeholders name the VALUES, so the locals carry those names:
        // a template that says {ProviderName} and an argument that says
        // provider.Name are the same value and two different names.
        string skillName = shadowed.Name;
        string providerName = provider.Name;
        string winnerProvider = winner.Provider;

        _logger.LogWarning(
            "Skill '{SkillName}' from provider '{ProviderName}' is shadowed by provider '{WinnerProvider}'.",
            skillName,
            providerName,
            winnerProvider);
    }
}
