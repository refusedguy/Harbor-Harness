using System.Text.RegularExpressions;
using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor skill list|install|uninstall</c> — manage SKILL.md skills.
///     Global skills live in <c>~/.harbor/skills</c>, project-local ones in
///     <c>&lt;cwd&gt;/.harbor/skills</c>. Each skill is either a
///     <c>&lt;name&gt;/SKILL.md</c> directory or a legacy flat
///     <c>&lt;name&gt;.md</c> file — the same shapes
///     <see cref="WorkspaceContextSource" /> discovers for the system prompt
///     and the <c>skill</c> builtin tool loads at runtime. Install copies a
///     reviewed file/directory into the target scope; uninstall removes it by
///     skill name. Operations are confined to the resolved skills roots.
/// </summary>
public sealed partial class SkillsCommand : ICommand
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _globalRoot;
    private readonly string _projectRoot;

    public SkillsCommand(TextWriter output, TextWriter error, string? globalRoot = null, string? projectRoot = null)
    {
        _output = output;
        _error = error;
        _globalRoot = globalRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "skills");
        _projectRoot = projectRoot ?? Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "skills");
    }

    public string Name => "skill";

    public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        return sub switch
        {
            "list" => ListAsync(),
            "install" => InstallAsync(args.Skip(1).ToArray()),
            "uninstall" or "remove" or "rm" => UninstallAsync(args.Skip(1).ToArray()),
            _ => PrintUsage()
        };
    }

    private Task<int> ListAsync()
    {
        var skills = WorkspaceContextSource.LoadSkillsFromRoots(_projectRoot, _globalRoot);
        if (skills.Count == 0)
        {
            _output.WriteLine("(no skills installed)");
            return Task.FromResult(0);
        }

        foreach (var skill in skills)
        {
            string scope = skill.FilePath.StartsWith(
                Path.GetFullPath(_projectRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? "project"
                : "global";
            _output.WriteLine($"{skill.Name} [{scope}] — {skill.Description}");
        }

        return Task.FromResult(0);
    }

    private async Task<int> InstallAsync(string[] args)
    {
        bool force = args.Contains("--force", StringComparer.Ordinal);
        bool toProject = args.Contains("--project", StringComparer.Ordinal);
        string? sourcePath = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (sourcePath is null)
        {
            await _error.WriteLineAsync("Usage: harbor skill install <SKILL.md|skill.md|skill-dir> [--project] [--force]").ConfigureAwait(false);
            return 2;
        }

        string fullPath = Path.GetFullPath(sourcePath);
        string root = toProject ? _projectRoot : _globalRoot;
        string rootFull = Path.GetFullPath(root);

        if (Directory.Exists(fullPath))
        {
            string skillFile = Path.Combine(fullPath, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                await _error.WriteLineAsync($"Directory has no SKILL.md: '{sourcePath}'").ConfigureAwait(false);
                return 1;
            }

            string name = Path.GetFileName(fullPath);
            if (!PlainSkillName().IsMatch(name))
            {
                await _error.WriteLineAsync($"Invalid skill directory name '{name}'.").ConfigureAwait(false);
                return 1;
            }

            string targetDir = ConfinementSafeResolve(root, name);
            if (!targetDir.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                await _error.WriteLineAsync("Resolved target escaped the skills root — refusing.").ConfigureAwait(false);
                return 1;
            }

            string targetFile = Path.Combine(targetDir, "SKILL.md");
            if (File.Exists(targetFile) && !force)
            {
                await _error.WriteLineAsync($"{targetFile} already exists; pass --force to overwrite.").ConfigureAwait(false);
                return 1;
            }

            Directory.CreateDirectory(targetDir);
            await File.WriteAllTextAsync(targetFile, await File.ReadAllTextAsync(skillFile).ConfigureAwait(false)).ConfigureAwait(false);
            await _output.WriteLineAsync($"Installed {(toProject ? "project" : "global")} skill → {targetFile}").ConfigureAwait(false);
            return 0;
        }

        if (!".md".Equals(Path.GetExtension(fullPath), StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            await _error.WriteLineAsync($"Not an installable skill (.md file or SKILL.md directory): '{sourcePath}'").ConfigureAwait(false);
            return 1;
        }

        string target = ConfinementSafeResolve(root, Path.GetFileName(sourcePath));
        if (!target.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            await _error.WriteLineAsync("Resolved target escaped the skills root — refusing.").ConfigureAwait(false);
            return 1;
        }

        if (File.Exists(target) && !force)
        {
            await _error.WriteLineAsync($"{target} already exists; pass --force to overwrite.").ConfigureAwait(false);
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, await File.ReadAllTextAsync(fullPath).ConfigureAwait(false)).ConfigureAwait(false);
        await _output.WriteLineAsync($"Installed {(toProject ? "project" : "global")} skill → {target}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> UninstallAsync(string[] args)
    {
        string? nameArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (nameArg is null || !PlainSkillName().IsMatch(nameArg))
        {
            await _error.WriteLineAsync("Usage: harbor skill uninstall <name>").ConfigureAwait(false);
            return 2;
        }

        foreach (string root in new[] { _projectRoot, _globalRoot })
        {
            string rootFull = Path.GetFullPath(root);
            var candidates = new[]
            {
                ConfinementSafeResolve(root, Path.Combine(nameArg, "SKILL.md")),
                ConfinementSafeResolve(root, nameArg + ".md"),
            };
            foreach (string candidate in candidates)
            {
                bool inside = candidate.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (!inside || !File.Exists(candidate))
                {
                    continue;
                }

                File.Delete(candidate);
                TryDeleteParentDirIfEmpty(Path.GetDirectoryName(candidate)!);
                await _output.WriteLineAsync($"Uninstalled {candidate}").ConfigureAwait(false);
                return 0;
            }
        }

        await _error.WriteLineAsync($"No skill named '{nameArg}' found in {_projectRoot} or {_globalRoot}.").ConfigureAwait(false);
        return 1;
    }

    private static void TryDeleteParentDirIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (IOException)
        {
            // Best-effort cleanup: a racing writer keeps the directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup: read-only entries stay in place.
        }
    }

    private Task<int> PrintUsage()
    {
        _error.WriteLine("""
                         Usage: harbor skill [list|install|uninstall]
                           skill list                        show global + project skills (project shadows global)
                           skill install <SKILL.md|dir>       copy into ~/.harbor/skills (--project targets <cwd>/.harbor/skills)
                           skill uninstall <name>            remove from either scope
                         """);
        return Task.FromResult(2);
    }

    /// <summary>Join inside the root only; segments like ".."/abs paths collapse under the root.</summary>
    private static string ConfinementSafeResolve(string root, string relative)
    {
        root = Path.GetFullPath(root);
        return Path.GetFullPath(Path.Combine(root, relative));
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainSkillName();
}
