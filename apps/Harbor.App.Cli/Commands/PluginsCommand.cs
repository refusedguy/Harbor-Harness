using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Application.Configuration;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Storage;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor plugin list|install|uninstall|config</c> — manage CS-source plugins.
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
            "config" or "settings" => ConfigAsync(args.Skip(1).ToArray()),
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

    // ── The settings form a plugin's schema generates (#1734) ─────────────────

    /// <summary>
    ///     Show what a plugin's settings are: the form its schema generates.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The point of the command is that the user does NOT have to guess
    ///         the shape of the section. The plugin declares its settings, and
    ///         this prints what it declared: the name, the type, the default,
    ///         the range the value has to fall in and the value the user has
    ///         set so far.
    ///     </para>
    ///     <para>
    ///         The section is checked against the schema before it is shown, so
    ///         a section that is wrong is reported with the setting that is
    ///         wrong. It is the same message the plugin host refuses the plugin
    ///         with, because it is the same check.
    ///     </para>
    /// </remarks>
    /// <param name="args">The command arguments: the plugin id, or nothing.</param>
    /// <returns>The exit code.</returns>
    private async Task<int> ConfigAsync(string[] args)
    {
        string? nameArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        IReadOnlyList<(string Name, PluginManifest Manifest)> manifests = await ReadManifestsAsync().ConfigureAwait(false);

        if (nameArg is null)
        {
            var declared = new List<(string Name, PluginManifest Manifest)>();
            foreach ((string name, PluginManifest manifest) in manifests)
            {
                if (manifest.Config is not null)
                {
                    declared.Add((name, manifest));
                }
            }

            if (declared.Count == 0)
            {
                await _output.WriteLineAsync("No installed plugin declares settings.").ConfigureAwait(false);
                return 0;
            }

            await _output.WriteLineAsync("Plugins that declare settings:").ConfigureAwait(false);
            foreach ((string name, PluginManifest manifest) in declared)
            {
                await _output.WriteLineAsync(
                    $"  {name}: {manifest.Config!.Fields.Count} setting(s): `harbor plugin config {name}`")
                    .ConfigureAwait(false);
            }

            return 0;
        }

        (string Name, PluginManifest Manifest)? found = null;
        foreach ((string name, PluginManifest manifest) in manifests)
        {
            if (string.Equals(name, nameArg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(manifest.Name, nameArg, StringComparison.OrdinalIgnoreCase))
            {
                found = (name, manifest);
                break;
            }
        }

        if (found is null)
        {
            await _error.WriteLineAsync($"No plugin named '{nameArg}' was found in {_globalRoot} or {_projectRoot}.")
                .ConfigureAwait(false);
            return 1;
        }

        PluginConfigSchema? schema = found.Value.Manifest.Config;
        if (schema is null || schema.Fields.Count == 0)
        {
            await _output.WriteLineAsync($"The plugin '{found.Value.Name}' declares no settings.").ConfigureAwait(false);
            return 0;
        }

        JsonElement? section = await ReadSectionAsync(found.Value.Name).ConfigureAwait(false);
        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved = PluginConfigValidator.Validate(
            schema,
            section.GetValueOrDefault(),
            $"plugins.{found.Value.Name}");
        if (resolved.IsFailure)
        {
            await _error.WriteLineAsync($"✗ {resolved.Error}").ConfigureAwait(false);
            return 1;
        }

        await _output.WriteLineAsync($"Settings of '{found.Value.Name}' (write them under plugins.{found.Value.Name}):")
            .ConfigureAwait(false);
        foreach (PluginConfigFormRow row in PluginConfigForm.Build(schema, resolved.Value))
        {
            await _output.WriteLineAsync("  " + Describe(row)).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    ///     One row of the settings form, as it is shown.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <returns>The written form.</returns>
    private static string Describe(PluginConfigFormRow row)
    {
        var parts = new List<string> { row.Name, row.Type.ToString().ToLowerInvariant() };

        if (row.AllowedValues is { Count: > 0 } allowed)
        {
            parts.Add("one of: " + string.Join(", ", allowed));
        }

        if (row.Minimum is not null || row.Maximum is not null)
        {
            parts.Add("range: "
                + (row.Minimum is { } low
                    ? low.ToString(CultureInfo.InvariantCulture)
                    : "-")
                + ".."
                + (row.Maximum is { } high
                    ? high.ToString(CultureInfo.InvariantCulture)
                    : "-"));
        }

        parts.Add("default: " + (row.Default?.ToString() ?? "(required)"));

        if (row.IsSecret)
        {
            parts.Add("current: (secret)");
        }
        else
        {
            parts.Add("current: " + (row.Current?.ToString() ?? "(not set)"));
        }

        if (!string.IsNullOrWhiteSpace(row.Description))
        {
            parts.Add("- " + row.Description);
        }

        return string.Join("  ", parts);
    }

    /// <summary>
    ///     Read the manifests of the installed plugins, from both scopes.
    /// </summary>
    /// <returns>The manifests, with the id each one is known by.</returns>
    private async Task<IReadOnlyList<(string Name, PluginManifest Manifest)>> ReadManifestsAsync()
    {
        var found = new List<(string Name, PluginManifest Manifest)>();

        foreach (string root in new[] { _projectRoot, _globalRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*" + PluginManifestReader.FileSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _error.WriteLineAsync($"Could not read '{root}': {ex.Message}").ConfigureAwait(false);
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await _error.WriteLineAsync($"Could not read '{file}': {ex.Message}").ConfigureAwait(false);
                    continue;
                }

                Result<PluginManifest> manifest = PluginManifestReader.Parse(text, Path.GetFileName(file));
                if (manifest.IsFailure)
                {
                    await _error.WriteLineAsync($"✗ {manifest.Error}").ConfigureAwait(false);
                    continue;
                }

                // The manifest is named after the source it describes, so the
                // file name without the suffix is the plugin's id when the
                // manifest does not declare one.
                string fileName = Path.GetFileName(file);
                string id = manifest.Value.Name
                    ?? (fileName.EndsWith(PluginManifestReader.FileSuffix, StringComparison.Ordinal)
                        ? fileName[..^PluginManifestReader.FileSuffix.Length]
                        : fileName);
                found.Add((id, manifest.Value));
            }
        }

        return found;
    }

    /// <summary>
    ///     Read the configuration section the user wrote for a plugin.
    /// </summary>
    /// <param name="pluginName">The plugin's id.</param>
    /// <returns>The section, or <see langword="null" /> when the user wrote none.</returns>
    private static async Task<JsonElement?> ReadSectionAsync(string pluginName)
    {
        var store = new JsonConfigStore();
        Result<HarborConfig> loaded = await store.LoadAsync().ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return null;
        }

        return loaded.Value.Plugins.TryGetValue(pluginName, out JsonElement section) ? section : null;
    }

    private Task<int> PrintUsage()
    {
        _error.WriteLine("""
                         Usage: harbor plugin [list|install|uninstall|config]
                           plugin list                       show global + project plugins
                           plugin install <file.cs>          copy into ~/.harbor/plugins (--project targets <cwd>/.harbor/plugins)
                           plugin uninstall <name>           remove from either scope
                           plugin config [<name>]            show a plugin's settings form
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
