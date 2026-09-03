using System.Text.RegularExpressions;
using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor skill list|install|uninstall</c> — manage skills.
///     Skills are Markdown files under <c>.harbor/skills/</c> (project-local
///     and/or <c>~/.harbor/skills/</c> (global).
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
            "install" => InstallAsync(args.Skip(1).ToArray(), ct),
            "uninstall" or "remove" or "rm" => UninstallAsync(args.Skip(1).ToArray(), ct),
            _ => PrintUsage()
        };
    }

    private Task<int> ListAsync()
    {
        _output.WriteLine($"global : {ListScope(_globalRoot)}");
        _output.WriteLine($"project: {ListScope(_projectRoot)}");
        return Task.FromResult(0);
    }

    private string ListScope(string root)
    {
        if (!Directory.Exists(root))
            return "(none)";

        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            return "(none)";

        return string.Join(", ", files.Select(Path.GetFileNameWithoutExtension)!);
    }

    private async Task<int> InstallAsync(string[] args, CancellationToken ct)
    {
        bool toProject = args.Contains("--project", StringComparer.Ordinal);
        bool force = args.Contains("--force", StringComparer.Ordinal);
        string? sourcePath = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (sourcePath is null)
        {
            await _error.WriteLineAsync("Usage: harbor skill install <file.md> [--project] [--force]").ConfigureAwait(false);
            return 2;
        }

        string fullPath = Path.GetFullPath(sourcePath);
        if (!".md".Equals(Path.GetExtension(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            await _error.WriteLineAsync($"Not an installable .md skill: '{sourcePath}'").ConfigureAwait(false);
            return 1;
        }

        if (!File.Exists(fullPath))
        {
            await _error.WriteLineAsync($"Skill file not found: '{sourcePath}'").ConfigureAwait(false);
            return 1;
        }

        string targetRoot = toProject ? _projectRoot : _globalRoot;
        string target = ConfinementSafeResolve(targetRoot, Path.GetFileName(fullPath));
        if (!target.StartsWith(
                Path.GetFullPath(targetRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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
        await File.WriteAllTextAsync(target, await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await _output.WriteLineAsync($"Installed {(toProject ? "project" : "global")} skill → {target}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> UninstallAsync(string[] args, CancellationToken ct)
    {
        string? nameArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (nameArg is null)
        {
            await _error.WriteLineAsync("Usage: harbor skill uninstall <name>").ConfigureAwait(false);
            return 2;
        }

        string fileName = PlainSkillName().IsMatch(nameArg)
            ? nameArg.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(nameArg)
                : Path.GetFileName(nameArg) + ".md"
            : string.Empty;

        foreach (string root in new[] { _projectRoot, _globalRoot })
        {
            string candidate = ConfinementSafeResolve(root, fileName);
            if (fileName.Length == 0 || !candidate.StartsWith(
                    Path.GetFullPath(root),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !File.Exists(candidate))
            {
                continue;
            }

            File.Delete(candidate);
            await _output.WriteLineAsync($"Uninstalled {candidate}").ConfigureAwait(false);
            return 0;
        }

        await _error.WriteLineAsync($"No skill named '{nameArg}' found in {_projectRoot} or {_globalRoot}.").ConfigureAwait(false);
        return 1;
    }

    private Task<int> PrintUsage()
    {
        _error.WriteLine("""
                         Usage: harbor skill [list|install|uninstall]
                           skill list                       show global + project skills
                           skill install <file.md>          copy into ~/.harbor/skills (--project targets <cwd>/.harbor/skills)
                           skill uninstall <name>           remove from either scope
                         """);
        return Task.FromResult(2);
    }

    /// <summary>Join inside the root only; segments like ".."/abs paths collapse under the root.</summary>
    private static string ConfinementSafeResolve(string root, string fileName)
    {
        root = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(root, fileName));
        return candidate;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(\.md)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainSkillName();
}
