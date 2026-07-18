using Harbor.Plugins.Abstractions;
using System.Reflection;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Compilation;

/// <summary>
///     <see cref="IPluginCompiler" /> decorator that caches compiled assemblies on disk
/// under <c>{cacheDir}/{sha256}.dll</c>. On a cache hit, the assembly is loaded via
/// <see cref="Assembly.LoadFrom(string)" /> and Roslyn is skipped entirely. On a cache
/// miss, the inner compiler is invoked and the resulting bytes (if compilation succeeds)
/// are persisted.
/// </summary>
/// <remarks>
///     <para>
///         Cache invalidation is purely content-based: renaming a source file does NOT
///         invalidate the cache. Editing the source does (hash changes). Orphaned cache
///         files (whose source was deleted) are NOT auto-cleaned — that's the user's
///         responsibility (see <c>harbor plugins gc</c> in the roadmap).
///     </para>
///     <para>
///         If a cached DLL cannot be loaded (e.g. host runtime upgraded and broke ABI),
///         the cache file is deleted and the inner compiler is invoked.
///     </para>
/// </remarks>
public sealed class CachingCompiler : IPluginCompiler
{
    private readonly IPluginCompiler _inner;
    private readonly string _cacheDir;
    private readonly ILogger<CachingCompiler> _logger;

    /// <summary>
    ///     Construct a new caching decorator.
    /// </summary>
    /// <param name="inner">Underlying compiler invoked on cache miss.</param>
    /// <param name="cacheDir">Directory to store cached DLLs.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public CachingCompiler(
        IPluginCompiler inner,
        string cacheDir,
        ILogger<CachingCompiler> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));

        string cachePath = System.IO.Path.Combine(_cacheDir, script.Hash + ".dll");

        if (File.Exists(cachePath))
        {
            try
            {
                // Assembly.LoadFrom is intentional here: the cache file lives on disk and
                // we want the runtime to resolve it as a path-based assembly so subsequent
                // Assembly.Load* calls for the same path hit the cache. Assembly.Load(byte[])
                // would re-load a fresh copy each time, defeating the cache.
#pragma warning disable S3885 // LoadFrom is intentional for the disk-cache path.
                var cachedAsm = Assembly.LoadFrom(cachePath);
#pragma warning restore S3885
                _logger.LogDebug("Cache hit for {Path} ({Hash})", script.Path, script.Hash);
                return CompilationResult.Cached(new CompiledPluginAssembly(
                    cachedAsm, script.Hash, script.Path, AssemblyBytes: null, FromCache: true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cached assembly load failed for {Path}, recompiling", script.Path);
                TryDelete(cachePath);
            }
        }

        var inner = await _inner.CompileAsync(script, ct).ConfigureAwait(false);
        if (inner.IsFailure)
            return inner;

        // Persist the freshly compiled assembly bytes for next time. The inner compiler
        // supplies the PE image via CompiledPluginAssembly.AssemblyBytes; if it didn't
        // (e.g. a custom compiler that only loads from a path), persistence is skipped.
        if (inner.Value.AssemblyBytes is { } bytes)
        {
            try
            {
                Directory.CreateDirectory(_cacheDir);
                await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                _logger.LogDebug("Wrote plugin cache {Path}", cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write plugin cache {Path}", cachePath);
            }
        }

        return inner;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* ignore — best-effort */ }
    }
}
