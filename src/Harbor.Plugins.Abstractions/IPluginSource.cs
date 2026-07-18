namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Async-stream source of <see cref="PluginScript" /> wrappers. Each implementation
///     represents a different storage backend for CS-source plugins (filesystem,
///     embedded resources, in-memory, network, git, etc.).
/// </summary>
/// <remarks>
///     <para>
///         The source layer is the only layer in the plugin runtime that knows <b>where
///         plugin scripts come from</b>. Everything downstream (compilation,
///         instantiation, registration) operates on already-materialized
///         <see cref="PluginScript" /> values and is storage-agnostic.
///     </para>
///     <para>
///         Implementations MUST be safe to enumerate concurrently from multiple
///         <see cref="GetScriptsAsync" /> calls. They SHOULD honor the supplied
///         <see cref="CancellationToken" /> promptly.
///     </para>
/// </remarks>
public interface IPluginSource
{
    /// <summary>
    ///     Enumerate the scripts produced by this source. Items are streamed as they are
    ///     discovered, allowing the host to start compiling early scripts while later
    ///     ones are still being read.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of <see cref="PluginScript" /> values.</returns>
    IAsyncEnumerable<PluginScript> GetScriptsAsync(CancellationToken ct = default);
}
