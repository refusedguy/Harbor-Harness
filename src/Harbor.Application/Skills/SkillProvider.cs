using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Sessions;

/// <summary>
///     Default <see cref="ISkillProvider" /> backed by <see cref="WorkspaceContextSource" />.
/// </summary>
public sealed class SkillProvider : ISkillProvider
{
    public IReadOnlyList<SkillDescriptor> GetSkills(string workingDirectory)
        => WorkspaceContextSource.LoadSkills(workingDirectory);

    public string? ReadSkill(string workingDirectory, string name)
    {
        var skills = WorkspaceContextSource.LoadSkills(workingDirectory);
        var skill = skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return null;

        try
        {
            return File.ReadAllText(skill.FilePath);
        }
        catch
        {
            return null;
        }
    }
}
