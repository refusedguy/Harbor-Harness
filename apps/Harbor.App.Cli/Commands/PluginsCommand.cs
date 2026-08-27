using System.Text.RegularExpressions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor plugin list|install|uninstall</c> — manage CS-source plugins.
///     Scope follows the loader split (<see cref="Harbor.Plugins.Storage.FileSystemPluginSource" />):
///     global plugins live in <c>~/.harbor/plugins</c>, project-local ones in
///     <c>&lt;cwd&gt;/.harbor/plugins</c>. Install copies a reviewed <c>.cs</c> file
///     into the target scope; uninstall removes it by file name (or name without
///     extension). Operations are confined to the resolved plugin roots — a
///     crafted name can never escape them.
/// </summary>
public sealed partial class PluginsCommand : ICommand
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _globalRoot;
    private readonly string _projectRoot;

    public PluginsCommand(TextWriter output, TextWriter error, string? globalRoot = null, string? projectRoot = null)
    {
        _output = output;
        _error = error;
        _globalRoot = globalRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "plugins");
        _projectRoot = projectRoot ?? Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");
    }

    public string Name => "plugin";

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
        _output.WriteLine($"global : {ListScope(_globalRoot)}");
        _output.WriteLine($"project: {ListScope(_projectRoot)}");
        return Task.FromResult(0);
    }

    private string ListScope(string root)
    {
        if (!Directory.Exists(root))
            return "(none)";

        var files = Directory.EnumerateFiles(root, "*.cs").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
            return "(none)";

        return string.Join(", ", files.Select(Path.GetFileName)!);
    }

    private async Task<int> InstallAsync(string[] args)
    {
        bool force = args.Contains("--force", StringComparer.Ordinal);
        bool toProject = args.Contains("--project", StringComparer.Ordinal);
        string? sourcePath = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (sourcePath is null)
        {
            await _error.WriteLineAsync("Usage: harbor plugin install <file.cs> [--project] [--force]").ConfigureAwait(false);
            return 2;
        }

        string fullPath = Path.GetFullPath(sourcePath);
        if (!".cs".Equals(Path.GetExtension(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            await _error.WriteLineAsync($"Not an installable .cs plugin source: '{sourcePath}'").ConfigureAwait(false);
            return 1;
        }

        if (!File.Exists(fullPath))
        {
            await _error.WriteLineAsync($"Plugin source not found: '{sourcePath}'").ConfigureAwait(false);
            return 1;
        }

        string target = ConfinementSafeResolve(toProject ? _projectRoot : _globalRoot, Path.GetFileName(sourcePath));
        if (!target.StartsWith(
                Path.GetFullPath(toProject ? _projectRoot : _globalRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            await _error.WriteLineAsync("Resolved target escaped the plugin root — refusing.").ConfigureAwait(false);
            return 1;
        }

        if (File.Exists(target) && !force)
        {
            await _error.WriteLineAsync($"{target} already exists; pass --force to overwrite.").ConfigureAwait(false);
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, await File.ReadAllTextAsync(fullPath).ConfigureAwait(false)).ConfigureAwait(false);
        await _output.WriteLineAsync($"Installed {(toProject ? "project" : "global")} plugin → {target}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> UninstallAsync(string[] args)
    {
        string? nameArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (nameArg is null)
        {
            await _error.WriteLineAsync("Usage: harbor plugin uninstall <name-or-file-name>").ConfigureAwait(false);
            return 2;
        }

        // Only the plain file name participates in matching; any directory part
        // in the argument would be a path-traversal attempt and is rejected.
        string fileName = PlainPluginName().IsMatch(nameArg)
            ? Path.GetFileName(nameArg.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? nameArg : nameArg + ".cs")
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

        await _error.WriteLineAsync($"No plugin named '{nameArg}' found in {_projectRoot} or {_globalRoot}.").ConfigureAwait(false);
        return 1;
    }

    private Task<int> PrintUsage()
    {
        _error.WriteLine("""
                         Usage: harbor plugin [list|install|uninstall]
                           plugin list                       show global + project plugins
                           plugin install <file.cs>          copy into ~/.harbor/plugins (--project targets <cwd>/.harbor/plugins)
                           plugin uninstall <name>           remove from either scope
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

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(\.cs)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPluginName();
}
