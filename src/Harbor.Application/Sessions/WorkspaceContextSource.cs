namespace Harbor.Core.Sessions;

/// <summary>
///     Discovers the workspace inputs of <see cref="SystemPromptContext" />:
///     project context files (<c>AGENTS.md</c>, <c>CLAUDE.md</c>) from the
///     session's working directory and skill files (<c>*.md</c>) from
///     <c>&lt;workdir&gt;/.harbor/skills</c> plus <c>~/.harbor/skills</c>
///     (ROP-C Z3 — the prompt builder rendered those sections, but the loop
///     hardcoded empty arrays).
/// </summary>
/// <remarks>
///     <para>
///         All lookups are existence-tolerant: a missing directory or file
///         yields an empty result, never a failure — a workspace without skills
///         is normal, not an error.
///     </para>
///     <para>
///         Skill format is deliberately minimal until specs define one: each
///         top-level <c>*.md</c> file is one skill; its description comes from a
///         front-matter <c>description:</c> line when present, otherwise from
///         the first non-empty line stripped of Markdown markers. Project-local
///         skills shadow global ones with the same file name.
///     </para>
///     <para>
///         Per-turn cost is bounded: two directory enumerations plus reading at
///         most the first few lines of every skill file and the full text of the
///         two context files. The expensive prompt assembly itself stays behind
///         <see cref="CachingSystemPromptBuilder" />'s content hash.
///     </para>
/// </remarks>
public static class WorkspaceContextSource
{
    /// <summary>Project context files probed in the working directory.</summary>
    public static readonly string[] ContextFileNames = ["AGENTS.md", "CLAUDE.md"];

    private const string ProjectSkillsDir = ".harbor/skills";
    private const int MaxDescriptionLength = 160;
    // Safety valve so a stray huge file cannot swallow the model's context window.
    private const long MaxContextFileBytes = 256 * 1024;

    /// <summary>Load project context files found in <paramref name="workingDirectory" />.</summary>
    public static IReadOnlyList<ContextFile> LoadContextFiles(string workingDirectory)
    {
        var files = new List<ContextFile>(ContextFileNames.Length);
        foreach (string name in ContextFileNames)
        {
            string path = Path.Combine(workingDirectory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > MaxContextFileBytes)
                {
                    continue;
                }

                files.Add(new ContextFile(name, File.ReadAllText(path)));
            }
            catch (IOException)
            {
                // Unreadable context file — skip it; the prompt builds without it.
            }
        }

        return files;
    }

    /// <summary>
    ///     Load available skills from the project-local and global skill
    ///     directories. Project-local entries win on name collisions.
    /// </summary>
    public static IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? globalSkillsDir = string.IsNullOrEmpty(userProfile)
            ? null
            : Path.Combine(userProfile, ProjectSkillsDir);
        return LoadSkills(workingDirectory, globalSkillsDir);
    }

    /// <summary>
    ///     Testable core: the global skill directory is injected instead of
    ///     resolved from the user profile.
    /// </summary>
    public static IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory, string? globalSkillsDir)
    {
        string projectSkillsDir = Path.Combine(workingDirectory, ProjectSkillsDir);

        var skills = new List<SkillDescriptor>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        // Project first so it shadows same-named global skills.
        CollectSkills(projectSkillsDir, skills, seenNames);
        CollectSkills(globalSkillsDir, skills, seenNames);
        return skills;
    }

    private static void CollectSkills(string? directory, List<SkillDescriptor> skills, HashSet<string> seenNames)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string[] skillFiles;
        try
        {
            skillFiles = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(skillFiles, StringComparer.Ordinal);

        foreach (string filePath in skillFiles)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            if (name.Length == 0 || !seenNames.Add(name))
            {
                continue;
            }

            skills.Add(new SkillDescriptor(name, ExtractDescription(filePath), filePath));
        }
    }

    /// <summary>
    ///     Read the skill's short description: front-matter
    ///     <c>description:</c> when present, else the first non-empty line
    ///     stripped of Markdown heading/list markers, capped to keep prompts lean.
    /// </summary>
    private static string ExtractDescription(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            bool inFrontMatter = false;
            int linesRead = 0;
            while (linesRead++ < 20 && reader.ReadLine() is { } line)
            {
                if (linesRead == 1 && line.TrimEnd() == "---")
                {
                    inFrontMatter = true;
                    continue;
                }

                if (inFrontMatter)
                {
                    if (line.TrimEnd() == "---")
                    {
                        inFrontMatter = false;
                        continue;
                    }

                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Cap(trimmed["description:".Length..].Trim());
                    }

                    continue;
                }

                string prose = line.TrimStart();
                if (prose.Length == 0)
                {
                    continue;
                }

                prose = prose.TrimStart('#', ' ', '-', '*').Trim();
                if (prose.Length > 0)
                {
                    return Cap(prose);
                }
            }
        }
        catch (IOException)
        {
            // Fall through to the generic descriptor below.
        }

        return "No description provided.";
    }

    private static string Cap(string description) =>
        description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength];
}
