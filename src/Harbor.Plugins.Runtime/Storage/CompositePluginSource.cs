using System.Runtime.CompilerServices;
namespace Harbor.Plugins.Runtime.Storage;

/// <summary>
///     <see cref="IPluginSource" /> that combines multiple underlying sources into a
///     single async stream. Sub-sources are enumerated sequentially in registration order;
///     de-duplication by <see cref="PluginScript.Hash" /> is the caller's responsibility
///     (the host layer handles it via the cache key).
/// </summary>
/// <remarks>
///     Useful for composing e.g. <see cref="FileSystemPluginSource" /> +
///     <see cref="EmbeddedResourcePluginSource" /> in the host composition root.
/// </remarks>
public sealed class CompositePluginSource : IPluginSource
{
    private readonly IReadOnlyList<IPluginSource> _sources;

    /// <summary>
    ///     Construct a composite source from the supplied sub-sources.
    /// </summary>
    /// <param name="sources">Sub-sources, in enumeration order.</param>
    public CompositePluginSource(IEnumerable<IPluginSource> sources)
    {
        if (sources is null)
            throw new ArgumentNullException(nameof(sources));
        _sources = sources.ToArray();
    }

    /// <summary>
    ///     Construct a composite source from the supplied sub-sources.
    /// </summary>
    /// <param name="sources">Sub-sources, in enumeration order.</param>
    public CompositePluginSource(params IPluginSource[] sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginScript> GetScriptsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var script in source.GetScriptsAsync(ct).ConfigureAwait(false))
            {
                yield return script;
            }
        }
    }
}
