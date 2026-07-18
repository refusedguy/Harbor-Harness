using System.Reflection;
namespace Harbor.Plugins.Runtime.Compilation;

/// <summary>
///     A successfully-compiled plugin assembly, plus the metadata needed by downstream
/// layers (instantiation, caching, logging).
/// </summary>
/// <param name="Assembly">The loaded <see cref="Assembly" />.</param>
/// <param name="SourceHash">
///     SHA-256 hex hash of the source text the assembly was compiled from. Used as the
///     cache key.
/// </param>
/// <param name="SourcePath">
///     Source identity (filesystem path, resource name, or synthetic id) — used for
///     diagnostics only.
/// </param>
/// <param name="AssemblyBytes">
///     Raw PE image of the compiled assembly, when available. Used by
///     <see cref="CachingCompiler" /> to persist to disk. May be <see langword="null" />
///     when the assembly was loaded from a path rather than from bytes (e.g. cache hit).
/// </param>
/// <param name="FromCache">
///     <see langword="true" /> if the assembly was loaded from the on-disk cache rather
///     than freshly compiled this run. Threads through to
///     <see cref="Instantiation.LoadedPlugin.LoadedFromCache" />.
/// </param>
public sealed record CompiledPluginAssembly(
    Assembly Assembly,
    string SourceHash,
    string SourcePath,
    byte[]? AssemblyBytes = null,
    bool FromCache = false);
