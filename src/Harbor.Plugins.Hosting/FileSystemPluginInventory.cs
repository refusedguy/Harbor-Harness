using System.Text;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     The plugin inventory over the two CS-source plugin scopes: the user's
///     own (<c>~/.harbor/plugins</c>) and the project's
///     (<c>&lt;cwd&gt;/.harbor/plugins</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>One implementation, two callers.</b> The agent-facing
///         <c>plugin_manager</c> tool and the <c>/plugins</c> panel both need
///         the same answer to "what is installed". This class is that answer;
///         a helper each of them called would be two implementations that
///         drift, and the drift would show up as an agent proposing to
///         uninstall a plugin the panel does not list.
///     </para>
///     <para>
///         <b>What it reads.</b> A plugin in Harbor is ONE <c>.cs</c> file, and
///         the facts about it come from two places: the FILE (which scope,
///         which path, whether it is enabled) and the MANIFEST it ships next
///         to itself (which axes it declares, which Harbor it needs). Both are
///         reported, because an agent deciding whether to install or remove a
///         plugin needs both and a file listing alone answers neither.
///     </para>
///     <para>
///         <b>Where the enumeration stops.</b> The scopes are walked
///         RECURSIVELY, because that is what the loader does
///         (<see cref="FileSystemPluginSource" /> walks subdirectories, and a
///         plugin may live in one). An inventory that listed only the top
///         level would report a plugin the host does not load as installed
///         and a plugin the host does load as absent.
///     </para>
///     <para>
///         <b>What it refuses to do.</b> Nothing outside the two roots is ever
///         read, written, renamed or deleted. A plugin is named by its plain
///         file name; a name carrying a directory part is refused before it
///         reaches the file system.
///     </para>
/// </remarks>
public sealed class FileSystemPluginInventory : IInstalledPluginInventory
{
    /// <summary>
    ///     The suffix that marks a disabled plugin source. The loader's glob
    ///     (<c>*.cs</c>) skips these files, so the suffix IS the switch.
    /// </summary>
    public const string DisabledSuffix = ".disabled";

    private readonly string _globalRoot;
    private readonly string _projectRoot;
    private readonly ILogger<FileSystemPluginInventory> _logger;

    /// <summary>
    ///     Construct the inventory over two plugin roots.
    /// </summary>
    /// <param name="globalRoot">The user's plugin scope (e.g. <c>~/.harbor/plugins</c>).</param>
    /// <param name="projectRoot">The project's plugin scope (e.g. <c>&lt;cwd&gt;/.harbor/plugins</c>).</param>
    /// <param name="logger">Logger for the scope-level diagnostics.</param>
    public FileSystemPluginInventory(
        string globalRoot,
        string projectRoot,
        ILogger<FileSystemPluginInventory> logger)
    {
        ArgumentNullException.ThrowIfNull(globalRoot);
        ArgumentNullException.ThrowIfNull(projectRoot);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _globalRoot = Path.GetFullPath(globalRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    /// <summary>
    ///     The user's plugin scope.
    /// </summary>
    public string GlobalRoot => _globalRoot;

    /// <summary>
    ///     The project's plugin scope.
    /// </summary>
    public string ProjectRoot => _projectRoot;

    /// <inheritdoc />
    public Result<IReadOnlyList<InstalledPluginEntry>> List()
    {
        var entries = new List<InstalledPluginEntry>();

        foreach ((PluginScope scope, string root) in Scopes())
        {
            string[] files;
            try
            {
                if (!Directory.Exists(root))
                    continue;
                files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                string[] disabled = Directory.GetFiles(root, "*.cs" + DisabledSuffix, SearchOption.AllDirectories);
                if (disabled.Length > 0)
                {
                    var all = new string[files.Length + disabled.Length];
                    files.CopyTo(all, 0);
                    disabled.CopyTo(all, files.Length);
                    files = all;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<IReadOnlyList<InstalledPluginEntry>>(
                    $"The {ScopeText(scope)} plugin scope '{root}' could not be read: {ex.Message} "
                    + "The plugin list is INCOMPLETE — fix the directory and retry; an incomplete list "
                    + "is never reported as 'no plugins'.");
            }

            for (int i = 0; i < files.Length; i++)
            {
                entries.Add(Describe(files[i], scope));
            }
        }

        entries.Sort(static (a, b) =>
        {
            int byScope = a.Scope.CompareTo(b.Scope);
            return byScope != 0 ? byScope : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return Result.Success<IReadOnlyList<InstalledPluginEntry>>(entries);
    }

    /// <inheritdoc />
    public Result<InstalledPluginEntry> DescribeSource(string sourcePath, PluginScope scope)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        if (!File.Exists(sourcePath))
        {
            return Result.Failure<InstalledPluginEntry>(
                $"The plugin source '{sourcePath}' does not exist. Next step: call list and install a source "
                + "that is on disk.");
        }

        if (!".cs".Equals(Path.GetExtension(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<InstalledPluginEntry>(
                $"'{sourcePath}' is not a plugin source: a CS-source plugin is one '.cs' file. "
                + "Next step: point the tool at a '.cs' file.");
        }

        string name = Path.GetFileName(sourcePath);
        string target = Path.Combine(RootOf(scope), name);
        return Result.Success(Describe(target, scope, sourcePath));
    }

    /// <inheritdoc />
    public async Task<Result<PluginInventoryChangeResult>> ApplyAsync(
        PluginInventoryChange change,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Action switch
        {
            PluginInventoryAction.Install => await InstallAsync(change, ct).ConfigureAwait(false),
            PluginInventoryAction.Uninstall => Uninstall(change),
            PluginInventoryAction.Enable => Toggle(change, enable: true),
            PluginInventoryAction.Disable => Toggle(change, enable: false),
            _ => Result.Failure<PluginInventoryChangeResult>(
                $"Unknown plugin action '{change.Action}'. Next step: call list and use one of "
                + "list, install, uninstall, enable, disable."),
        };
    }

    /// <summary>
    ///     Copy a reviewed source into a scope, together with the manifest it
    ///     ships next to it.
    /// </summary>
    private async Task<Result<PluginInventoryChangeResult>> InstallAsync(
        PluginInventoryChange change,
        CancellationToken ct)
    {
        string? source = change.SourcePath;
        if (string.IsNullOrWhiteSpace(source))
        {
            return Result.Failure<PluginInventoryChangeResult>(
                "An install needs the path of the plugin source to install. Next step: call list to see "
                + "what is installed, and pass 'source' with the '.cs' file to install.");
        }

        Result<InstalledPluginEntry> described = DescribeSource(source, change.Scope);
        if (described.IsFailure)
        {
            // The explicit form: the refusal is traced here, not only re-typed.
            // The inventory is the layer that knows why the source cannot be
            // installed, and the reason belongs in the log as well as in the
            // tool's answer.
            _logger.LogDebug(
                "The plugin source '{Source}' cannot be installed: {Error}",
                source,
                described.Error);
            return described.ConvertFailure<PluginInventoryChangeResult>();
        }

        InstalledPluginEntry entry = described.Value;

        // The target is the name that was PROPOSED, not the name the source
        // happens to have: the human approved a name, and the copy must land
        // where the proposal said it would.
        if (!IsPlainName(change.Name))
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{change.Name}' is not a plugin file name. Next step: call list and use the exact file "
                + "name it reports (e.g. 'TodoWrite.cs').");
        }

        string target = Path.Combine(RootOf(change.Scope), change.Name);
        if (!entry.Compatible)
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{entry.Name}' declares Harbor >= {entry.RequiredHarborVersion}, but this Harbor is "
                + $"{HarborRuntimeVersion.Current}. The plugin was NOT installed — the host would refuse "
                + "it at load time. Next step: install a plugin that supports this Harbor, or report the "
                + "diagnostic to the user.");
        }

        if (entry.ManifestError is { } manifestError)
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"The manifest of '{entry.Name}' could not be read: {manifestError} The plugin was NOT "
                + "installed — the host refuses a plugin whose declaration it cannot apply. Next step: fix "
                + "the manifest, or install the plugin without it.");
        }

        if (File.Exists(target))
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{change.Name}' is already installed in the {ScopeText(change.Scope)} scope "
                + $"({target}). Next step: call list and uninstall it first, or install it under another "
                + "name.");
        }

        try
        {
            Directory.CreateDirectory(RootOf(change.Scope));
            await CopyFileAsync(source, target, overwrite: false, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{change.Name}' could not be installed into {target}: {ex.Message} "
                + "Next step: report the diagnostic to the user.");
        }

        // The manifest travels with the source: a plugin installed without its
        // declaration is a plugin whose constraints the host cannot apply.
        string sourceManifest = PluginManifestReader.PathFor(source);
        if (File.Exists(sourceManifest))
        {
            string targetManifest = PluginManifestReader.PathFor(target);
            try
            {
                await CopyFileAsync(sourceManifest, targetManifest, overwrite: true, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<PluginInventoryChangeResult>(
                    $"'{change.Name}' was installed, but its manifest could not be copied: {ex.Message} "
                    + "Next step: report the diagnostic to the user — the plugin host refuses a plugin "
                    + "whose manifest is missing.");
            }
        }

        return Result.Success(new PluginInventoryChangeResult(
            PluginInventoryAction.Install,
            change.Name,
            target,
            $"Installed '{change.Name}' into the {ScopeText(change.Scope)} scope ({target})."));
    }

    /// <summary>
    ///     Copy one file to another, asynchronously.
    /// </summary>
    /// <remarks>
    ///     The BCL has no <c>File.CopyAsync</c>, so the copy is what it always
    ///     was under the hood: a read stream and a write stream, copied
    ///     through. <see cref="FileMode.CreateNew" /> is what makes the
    ///     "overwrite: false" case atomic — the target is created or the copy
    ///     fails, never both.
    /// </remarks>
    private static async Task CopyFileAsync(
        string source,
        string target,
        bool overwrite,
        CancellationToken ct)
    {
        await using FileStream from = File.OpenRead(source);
        await using FileStream to = new FileStream(
            target,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await from.CopyToAsync(to, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     The manifest that belongs to a plugin file.
    /// </summary>
    /// <remarks>
    ///     The manifest is named after the ENABLED file: <c>Alpha.cs</c> ships
    ///     <c>Alpha.harbor-plugin.json</c>, and a disabled
    ///     <c>Alpha.cs.disabled</c> keeps the manifest it was installed with.
    ///     <c>PathFor</c> strips only the LAST extension, so the disabled form
    ///     has to be normalised here first.
    /// </remarks>
    private static string ManifestPathFor(string pluginPath) =>
        PluginManifestReader.PathFor(
            pluginPath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? pluginPath[..^DisabledSuffix.Length]
                : pluginPath);

    /// <summary>
    ///     Remove an installed plugin — its source and, when it ships one, its
    ///     manifest.
    /// </summary>
    private Result<PluginInventoryChangeResult> Uninstall(PluginInventoryChange change)
    {
        return Find(change.Name).Bind(found => UninstallCore(found.Entry, found.Path));
    }

    /// <summary>
    ///     Remove the plugin's source and, when it ships one, its manifest.
    /// </summary>
    private Result<PluginInventoryChangeResult> UninstallCore(InstalledPluginEntry entry, string path)
    {
        try
        {
            File.Delete(path);
            string manifest = ManifestPathFor(path);
            if (File.Exists(manifest))
                File.Delete(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{entry.Name}' could not be removed ({path}): {ex.Message} Next step: report the "
                + "diagnostic to the user.");
        }

        return Result.Success(new PluginInventoryChangeResult(
            PluginInventoryAction.Uninstall,
            entry.Name,
            path,
            $"Removed '{entry.Name}' from the {ScopeText(entry.Scope)} scope ({path})."));
    }

    /// <summary>
    ///     Toggle a plugin by renaming it to or from the <c>.disabled</c> form
    ///     the loader's glob skips.
    /// </summary>
    private Result<PluginInventoryChangeResult> Toggle(PluginInventoryChange change, bool enable)
    {
        return Find(change.Name).Bind(found => ToggleCore(found.Entry, found.Path, enable));
    }

    /// <summary>
    ///     Rename the plugin's source to or from the <c>.disabled</c> form the
    ///     loader's glob skips.
    /// </summary>
    private Result<PluginInventoryChangeResult> ToggleCore(
        InstalledPluginEntry entry,
        string path,
        bool enable)
    {
        if (entry.Enabled == enable)
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{entry.Name}' is already {(enable ? "enabled" : "disabled")}. Next step: nothing to do, "
                + "or call list to see the current state.");
        }

        string target = enable
            ? path[..^DisabledSuffix.Length]
            : path + DisabledSuffix;

        try
        {
            File.Move(path, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<PluginInventoryChangeResult>(
                $"'{entry.Name}' could not be {(enable ? "enabled" : "disabled")} ({path}): {ex.Message} "
                + "Next step: report the diagnostic to the user.");
        }

        return Result.Success(new PluginInventoryChangeResult(
            enable ? PluginInventoryAction.Enable : PluginInventoryAction.Disable,
            entry.Name,
            target,
            $"{(enable ? "Enabled" : "Disabled")} '{entry.Name}' in the {ScopeText(entry.Scope)} scope "
            + $"({target})."));
    }

    /// <summary>
    ///     Find an installed plugin by its plain file name — the project scope
    ///     first, then the user's, which is the order the CLI's
    ///     <c>harbor plugin uninstall</c> uses.
    /// </summary>
    private Result<(InstalledPluginEntry Entry, string Path)> Find(string name)
    {
        if (!IsPlainName(name))
        {
            return Result.Failure<(InstalledPluginEntry Entry, string Path)>(
                $"'{name}' is not a plugin file name. Next step: call list and use the exact file name it "
                + "reports (e.g. 'TodoWrite.cs').");
        }

        foreach ((PluginScope scope, string root) in Scopes())
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path) && !File.Exists(path + DisabledSuffix))
                continue;

            string actual = File.Exists(path) ? path : path + DisabledSuffix;
            return Result.Success((Describe(actual, scope), actual));
        }

        return Result.Failure<(InstalledPluginEntry Entry, string Path)>(
            $"No plugin named '{name}' is installed in {_projectRoot} or {_globalRoot}. Next step: call list "
            + "and use the exact file name it reports.");
    }

    /// <summary>
    ///     Read one plugin file — and the manifest next to it — into an entry.
    /// </summary>
    /// <param name="path">The plugin's source file, possibly the <c>.disabled</c> form.</param>
    /// <param name="scope">The scope the file lives in.</param>
    /// <param name="manifestSource">
    ///     The file the manifest is read from. It differs from
    ///     <paramref name="path" /> for a disabled plugin: the manifest belongs
    ///     to the ENABLED name, and a disabled plugin keeps the manifest it was
    ///     installed with.
    /// </param>
    private InstalledPluginEntry Describe(string path, PluginScope scope, string? manifestSource = null)
    {
        string full = Path.GetFullPath(path);
        bool enabled = !full.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        string name = Path.GetFileName(enabled ? full : full[..^DisabledSuffix.Length]);
        string source = manifestSource ?? (enabled ? full : full[..^DisabledSuffix.Length]);

        string manifestPath = PluginManifestReader.PathFor(source);
        if (!File.Exists(manifestPath))
        {
            return new InstalledPluginEntry(name, scope, full, enabled, null, [], null, true);
        }

        string text;
        try
        {
            // Bytes, not text: the loader reads the manifest the same way, and
            // a byte-for-byte read keeps the two readings identical (a BOM is
            // part of the document for both).
            text = Encoding.UTF8.GetString(File.ReadAllBytes(manifestPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstalledPluginEntry(name, scope, full, enabled, null, [], null, true)
            {
                ManifestError = $"the manifest '{manifestPath}' could not be read: {ex.Message}",
            };
        }

        Result<PluginManifest> manifest = PluginManifestReader.Parse(text, manifestPath, _logger);
        if (manifest.IsFailure)
        {
            return new InstalledPluginEntry(name, scope, full, enabled, null, [], null, true)
            {
                ManifestError = manifest.Error,
            };
        }

        PluginManifest value = manifest.Value;
        string? floor = value.RequiredHarborVersion?.ToString();
        bool compatible = PluginCompatibility.IsCompatible(HarborRuntimeVersion.Current, value.RequiredHarborVersion);
        IReadOnlyList<string> axes = value.Axes ?? [];

        return new InstalledPluginEntry(
            name,
            scope,
            full,
            enabled,
            value.Version?.ToString(),
            axes,
            floor,
            compatible);
    }

    /// <summary>
    ///     The two scopes, in the order a name is resolved: the project first,
    ///     then the user's.
    /// </summary>
    private (PluginScope Scope, string Root)[] Scopes() =>
    [
        (PluginScope.Project, _projectRoot),
        (PluginScope.Global, _globalRoot),
    ];

    /// <summary>
    ///     The root of one scope.
    /// </summary>
    private string RootOf(PluginScope scope) =>
        scope == PluginScope.Global ? _globalRoot : _projectRoot;

    /// <summary>
    ///     A scope's name as the model reads it.
    /// </summary>
    internal static string ScopeText(PluginScope scope) =>
        scope == PluginScope.Global ? "global" : "project";

    /// <summary>
    ///     Plain file names only — no separators, no parent traversal. A name
    ///     that fails this never reaches the file system.
    /// </summary>
    internal static bool IsPlainName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool ok = char.IsLetterOrDigit(c) || c is '-' or '_' or '.';
            if (!ok)
                return false;
        }

        return !name.Contains("..", StringComparison.Ordinal);
    }
}
