using System.Runtime.CompilerServices;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Storage;
/// <summary>
///     <see cref="IPluginSource" /> that discovers <c>.cs</c> files under one or more
///     filesystem directories, including subdirectories. Each file is loaded into
///     a <see cref="PluginScript" /> via <see cref="PluginScript.LoadAsync" /> and
///     still compiles as one plugin on its own (single-file contract: files that
///     reference each other's types fail compilation, loudly, instead of loading).
/// </summary>
/// <remarks>
///     <para>
///         Directories that do not exist are silently skipped (logged at debug level).
///         Files are de-duplicated by absolute path: if the same directory is supplied
///         twice or two directories overlap, each file is emitted exactly once.
///     </para>
///     <para>
///         This is the default plugin source used by the Harbor CLI. It scans
///         <c>~/.harbor/plugins/</c> and <c>&lt;cwd&gt;/.harbor/plugins/</c> recursively:
///         a plugin may live in a subdirectory (e.g. for organization), but it must
///         stay self-contained in its own file. See #1046.
///     </para>
///     <para>
///         <b>Manifests are read here (#1739).</b> A plugin may ship a JSON
///         manifest next to its source, named after it
///         (<c>MyPlugin.cs</c> → <c>MyPlugin.harbor-plugin.json</c>). The manifest
///         is read as data and travels with the script, so the host can refuse an
///         incompatible plugin before it pays to compile it. A plugin without a
///         manifest is unaffected.
///     </para>
/// </remarks>
public sealed class FileSystemPluginSource : IPluginSource
{
    private readonly IReadOnlyList<string> _directories;
    private readonly ILogger<FileSystemPluginSource> _logger;

    /// <summary>
    ///     Construct a new filesystem plugin source.
    /// </summary>
    /// <param name="directories">Directories to scan for <c>.cs</c> files.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public FileSystemPluginSource(
        IEnumerable<string> directories,
        ILogger<FileSystemPluginSource> logger)
    {
        if (directories is null)
            throw new ArgumentNullException(nameof(directories));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directories = directories.ToArray();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginScript> GetScriptsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in _directories)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;

            string[] files;
            try
            {
                // Recursive: a plugin may live in a subdirectory, but each file is
                // still discovered (and compiled) on its own. See #1046.
                files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to enumerate plugin directory {Dir}", dir);
                continue;
            }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(file))
                    continue;

                var load = await PluginScript.LoadAsync(file, ct).ConfigureAwait(false);
                if (load.IsSuccess)
                {
                    yield return await WithManifestAsync(load.Value, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("Failed to load plugin source {Path}: {Error}", file, load.Error);
                }
            }
        }
    }

    /// <summary>
    ///     Attach the manifest that belongs to a plugin source, when the plugin
    ///     ships one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The manifest is a file NEXT TO the source, named after it
    ///         (<c>MyPlugin.cs</c> → <c>MyPlugin.harbor-plugin.json</c>). It is
    ///         read here, at the layer that knows where plugin files come from,
    ///         and it travels with the script from this point on.
    ///     </para>
    ///     <para>
    ///         A manifest that cannot be read is NOT dropped here: the script
    ///         carries the reason, and the host refuses the plugin with that
    ///         reason. Dropping it at discovery would make a broken manifest
    ///         look like a plugin that does not exist.
    ///     </para>
    /// </remarks>
    /// <param name="script">The loaded plugin source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The script with its manifest attached.</returns>
    private async Task<PluginScript> WithManifestAsync(PluginScript script, CancellationToken ct)
    {
        string manifestPath = PluginManifestReader.PathFor(script.Path);
        if (!File.Exists(manifestPath))
            return script;

        var read = await PluginManifestReader
            .ReadAsync(manifestPath, _logger, ct)
            .ConfigureAwait(false);

        if (read.IsSuccess)
        {
            return script.WithManifest(read.Value);
        }

        _logger.LogWarning(
            "The plugin manifest {Manifest} could not be read: {Error} The plugin was not loaded.",
            manifestPath,
            read.Error);
        return script.WithManifest(null, read.Error);
    }
}
