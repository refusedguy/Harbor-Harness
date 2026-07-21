using System.Runtime.CompilerServices;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Storage;
/// <summary>
///     <see cref="IPluginSource" /> that discovers <c>.cs</c> files under one or more
///     filesystem directories. Each file is loaded into a <see cref="PluginScript" />
///     via <see cref="PluginScript.LoadAsync" />.
/// </summary>
/// <remarks>
///     <para>
///         Directories that do not exist are silently skipped (logged at debug level).
///         Files are de-duplicated by absolute path: if the same directory is supplied
///         twice or two directories overlap, each file is emitted exactly once.
///     </para>
///     <para>
///         This is the default plugin source used by the Harbor CLI. It scans
///         <c>~/.harbor/plugins/*.cs</c> and <c>&lt;cwd&gt;/.harbor/plugins/*.cs</c>.
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
                files = Directory.GetFiles(dir, "*.cs");
            }
            catch (IOException ex)
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
                    yield return load.Value;
                }
                else
                {
                    _logger.LogWarning("Failed to load plugin source {Path}: {Error}", file, load.Error);
                }
            }
        }
    }
}
