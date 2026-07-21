using Microsoft.CodeAnalysis;
namespace Harbor.Plugins.Abstractions;
/// <summary>
///     Compiles a single <see cref="PluginScript" /> into a loaded
///     <see cref="CompiledPluginAssembly" />. Implementations encapsulate a specific
///     compilation strategy — Roslyn in-memory, scripted evaluator, external process, etc.
/// </summary>
/// <remarks>
///     <para>
///         Implementations MUST be stateless across calls — all per-script state lives
///         in the returned <see cref="CompiledPluginAssembly" />. The cache-decorator
///         <see cref="CachingCompiler" /> wraps an inner compiler to skip compilation when a
///         cached assembly exists.
///     </para>
///     <para>
///         Implementations SHOULD NOT call <see cref="System.Reflection.Assembly.LoadFrom" />
///         themselves unless they own the bytes (cache hit path). Fresh bytes are loaded
///         via <see cref="System.Reflection.Assembly.Load(byte[])" /> to avoid leaking
///         files on disk into the AppDomain's path-resolution graph.
///     </para>
/// </remarks>
public interface IPluginCompiler
{
    /// <summary>
    ///     Compile (or otherwise materialize) the supplied script into a loaded assembly.
    /// </summary>
    /// <param name="script">The plugin source to compile.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success with the loaded assembly + source hash, or failure with a
    ///     human-readable error message. On failure, <see cref="CompilationResult.Diagnostics" />
    ///     MAY carry the underlying Roslyn diagnostics (empty if not applicable).
    /// </returns>
    public Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default);
}

/// <summary>
///     Result of an <see cref="IPluginCompiler.CompileAsync" /> call. Success carries a
///     <see cref="CompiledPluginAssembly" />; failure carries an error string and (optionally)
///     Roslyn diagnostics.
/// </summary>
public readonly record struct CompilationResult
{
    private readonly CompiledPluginAssembly? _assembly;
    private readonly IReadOnlyList<Diagnostic>? _diagnostics;
    private readonly string? _error;

    private CompilationResult(
        CompiledPluginAssembly? assembly,
        string? error,
        IReadOnlyList<Diagnostic>? diagnostics,
        bool fromCache)
    {
        _assembly = assembly;
        _error = error;
        _diagnostics = diagnostics;
        FromCache = fromCache;
    }

    /// <summary>Whether the compilation succeeded.</summary>
    public bool IsSuccess => _assembly is not null;

    /// <summary>Whether the compilation failed.</summary>
    public bool IsFailure => _assembly is null;

    /// <summary>The compiled assembly (only valid when <see cref="IsSuccess" />).</summary>
    public CompiledPluginAssembly Value => _assembly ?? throw new InvalidOperationException("CompilationResult is failure.");

    /// <summary>The error message (only valid when <see cref="IsFailure" />).</summary>
    public string Error => _error ?? string.Empty;

    /// <summary>
    ///     Roslyn diagnostics emitted during compilation (warnings + errors). Empty on
    ///     success-only paths or when the failure occurred before reaching the compiler.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics ?? Array.Empty<Diagnostic>();

    /// <summary>
    ///     <see langword="true" /> if the assembly was loaded from a cache rather than
    ///     freshly compiled this call. Used by the host to populate
    ///     <see cref="CompiledPlugin.LoadedFromCache" />.
    /// </summary>
    public bool FromCache
    {
        get;
    }

    /// <summary>Create a successful fresh-compile result.</summary>
    public static CompilationResult Fresh(CompiledPluginAssembly asm) =>
        new(asm, null, null, false);

    /// <summary>Create a successful cache-hit result.</summary>
    public static CompilationResult Cached(CompiledPluginAssembly asm) =>
        new(asm, null, null, true);

    /// <summary>Create a failed result with diagnostics.</summary>
    public static CompilationResult Failure(string error, IReadOnlyList<Diagnostic> diagnostics) =>
        new(null, error, diagnostics, false);

    /// <summary>Create a failed result without diagnostics.</summary>
    public static CompilationResult Failure(string error) =>
        new(null, error, Array.Empty<Diagnostic>(), false);
}
