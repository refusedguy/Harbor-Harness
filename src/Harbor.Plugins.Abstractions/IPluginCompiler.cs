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
///     <para>
///         Whether the assembly came from the on-disk cache is a property of the value —
///         <see cref="CompiledPluginAssembly.FromCache" /> — not of the outcome, because
///         "freshly compiled" and "loaded from cache" are both successes. The previous
///         hand-rolled <c>CompilationResult</c> struct carried the same flag twice, and
///         carried a third value: an <c>Error</c> member that returned
///         <see cref="string.Empty" /> on a success, so <c>if (x.IsFailure) log(x.Error)</c>
///         and <c>if (x.IsSuccess) log(x.Error)</c> both produced plausible output and a
///         caller could not tell a real failure from an empty one. A
///         <see cref="Result{T}" /> cannot represent that state: <c>Result.Failure&lt;T&gt;("")</c>
///         throws <see cref="ArgumentNullException" />, and reading <c>Error</c> on a success
///         throws <see cref="System.InvalidOperationException" />. See #561.
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
    ///     human-readable error message. The failure message IS the rendered form of the
    ///     underlying Roslyn diagnostics — severity, file, line, column, code and text,
    ///     one per line — because that string is what the host logs and surfaces. There is
    ///     deliberately no second, structured channel for the same information: a member
    ///     carrying it existed only for tests to read, and had no production reader at all.
    /// </returns>
    public Task<Result<CompiledPluginAssembly>> CompileAsync(PluginScript script, CancellationToken ct = default);
}
