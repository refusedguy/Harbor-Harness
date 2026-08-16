using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Harbor.Plugins.Abstractions;
namespace Harbor.Plugins.Storage;
/// <summary>
///     <see cref="IPluginSource" /> backed by an in-memory collection of
///     <see cref="PluginScript" /> values. Intended primarily for tests and for embedding
///     plugins authored in code rather than on disk.
/// </summary>
/// <remarks>
///     Thread-safe: scripts can be added before or during enumeration. The source
///     enumerates a point-in-time snapshot.
/// </remarks>
public sealed class InMemoryPluginSource : IPluginSource
{
    private readonly ConcurrentQueue<PluginScript> _scripts = new();

    /// <summary>
    ///     Construct an empty in-memory source.
    /// </summary>
    public InMemoryPluginSource() { }

    /// <summary>
    ///     Construct an in-memory source pre-populated with the supplied scripts.
    /// </summary>
    /// <param name="scripts">Initial script set.</param>
    public InMemoryPluginSource(IEnumerable<PluginScript> scripts)
    {
        if (scripts is null)
            throw new ArgumentNullException(nameof(scripts));
        foreach (var s in scripts)
            _scripts.Enqueue(s);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginScript> GetScriptsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Snapshot the queue to avoid yielding while another thread mutates.
        var snapshot = _scripts.ToArray();
        foreach (var script in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return await Task.FromResult(script).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Add a script to the source. The script's <see cref="PluginScript.Path" /> is
    ///     used as a synthetic identity; nothing is read from disk.
    /// </summary>
    /// <param name="script">The script to add.</param>
    public void Add(PluginScript script)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));
        _scripts.Enqueue(script);
    }

    /// <summary>
    ///     Add a new script from raw source text. The <paramref name="path" /> is used
    ///     only as the script's identity — no file is read.
    /// </summary>
    /// <param name="path">Synthetic path / identity for the script.</param>
    /// <param name="source">Raw CS source text.</param>
    public void Add(string path, string source)
        => Add(new PluginScript(path, source));
}
