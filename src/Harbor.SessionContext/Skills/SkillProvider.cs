using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;

namespace Harbor.Application.Skills;

/// <summary>
///     Default <see cref="ISkillProvider" /> backed by <see cref="WorkspaceContextSource" />.
/// </summary>
/// <remarks>
///     <para>
///         THE WORKSPACE PROVIDER. It reads the two fixed roots — the
///         project-local <c>&lt;workdir&gt;/.harbor/skills</c> and the global
///         <c>~/.harbor/skills</c> — and the project root wins on a name
///         collision. That layer rule stays HERE, inside the provider that
///         owns the two roots; it is not the registry's rule. The registry's
///         rule is about providers competing with each other, and a second
///         source (a plugin's catalog, a remote service) can be added without
///         touching this file — which is the whole point of the split.
///     </para>
///     <para>
///         The provider also carries each skill's INVOCATION POLICY, read
///         from the skill file's front-matter
///         (<c>model-invocable:</c> / <c>user-invocable:</c>). A skill that
///         declares neither is invocable by both surfaces, so the flags change
///         nothing for the skills that do not ask for it.
///     </para>
/// </remarks>
public sealed class SkillProvider : ISkillProvider
{
    /// <summary>The provider's name. Unique among the registered providers.</summary>
    public const string WorkspaceProviderName = "workspace";

    /// <summary>The provider's name. Unique among the registered providers.</summary>
    public string Name => WorkspaceProviderName;

    /// <summary>
    ///     The provider's rank. The workspace roots are the product's own
    ///     source, so they outrank a source that is layered on top of them —
    ///     but the number is declared, not implied by registration order.
    /// </summary>
    public int Priority => 100;

    /// <summary>How many lines of a skill file the front-matter policy is read from.</summary>
    private const int FrontMatterScanLines = 20;

    private readonly object _policyGate = new();
    private readonly Dictionary<string, (IReadOnlyList<SkillDescriptor> Source, IReadOnlyList<SkillDescriptor> Enriched)>
        _policyCache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<SkillDescriptor> GetSkills(string workingDirectory)
    {
        // The cached snapshot is the SAME list instance until the skills roots
        // change on disk, so its identity is the cache key: the front-matter
        // read below happens once per change, not once per turn.
        IReadOnlyList<SkillDescriptor> source = WorkspaceContextSource.GetOrLoadCached(workingDirectory).Skills;

        lock (_policyGate)
        {
            if (_policyCache.TryGetValue(workingDirectory, out (IReadOnlyList<SkillDescriptor> Source, IReadOnlyList<SkillDescriptor> Enriched) hit)
                && ReferenceEquals(hit.Source, source))
            {
                return hit.Enriched;
            }

            IReadOnlyList<SkillDescriptor> enriched = ApplyDeclaredPolicy(source);
            _policyCache[workingDirectory] = (source, enriched);
            return enriched;
        }
    }

    /// <inheritdoc />
    public string? ReadSkill(string workingDirectory, string name)
    {
        foreach (SkillDescriptor skill in GetSkills(workingDirectory))
        {
            if (!string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                return File.ReadAllText(skill.FilePath);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Read each skill's declared invocation policy from its file's
    ///     front-matter. A file that declares neither field keeps the
    ///     descriptor's own flags.
    /// </summary>
    private static IReadOnlyList<SkillDescriptor> ApplyDeclaredPolicy(IReadOnlyList<SkillDescriptor> skills)
    {
        if (skills.Count == 0)
        {
            return skills;
        }

        var enriched = new SkillDescriptor[skills.Count];
        for (int i = 0; i < skills.Count; i++)
        {
            SkillDescriptor skill = skills[i];
            enriched[i] = ReadDeclaredPolicy(skill) ?? skill;
        }

        return enriched;
    }

    private static SkillDescriptor? ReadDeclaredPolicy(SkillDescriptor skill)
    {
        bool? model = null;
        bool? user = null;

        try
        {
            using var reader = new StreamReader(skill.FilePath);
            bool inFrontMatter = false;
            int linesRead = 0;

            while (linesRead++ < FrontMatterScanLines && reader.ReadLine() is { } line)
            {
                if (linesRead == 1 && line.TrimEnd() == "---")
                {
                    inFrontMatter = true;
                    continue;
                }

                if (!inFrontMatter)
                {
                    // No front-matter at all: nothing can be declared.
                    return null;
                }

                if (line.TrimEnd() == "---")
                {
                    // The front-matter ended and declared no policy field.
                    break;
                }

                if (FrontMatter.GetFrontMatterField(line, "model-invocable") is { } modelField)
                {
                    model = ParseBoolean(modelField);
                }

                if (FrontMatter.GetFrontMatterField(line, "user-invocable") is { } userField)
                {
                    user = ParseBoolean(userField);
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (model is null && user is null)
        {
            return null;
        }

        return skill with
        {
            ModelInvocable = model ?? skill.ModelInvocable,
            UserInvocable = user ?? skill.UserInvocable,
        };
    }

    /// <summary>
    ///     Parse a front-matter boolean. Anything that is not recognisably
    ///     true or false is ignored, so a typo leaves the default in place
    ///     rather than silently disabling a skill.
    /// </summary>
    private static bool? ParseBoolean(string value)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("0", StringComparison.Ordinal))
        {
            return false;
        }

        return null;
    }
}
