using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Compilation;

/// <summary>
///     <see cref="IPluginCompiler" /> descriptor that builds the inner compiler on
///     the first <see cref="CompileAsync" /> call instead of at composition time
///     (#1055 slice 1, graceful absence).
/// </summary>
/// <remarks>
///     <para>
///         The default inner compiler is Roslyn, and constructing it snapshots the
///         <see cref="AppDomain" /> into <c>Microsoft.CodeAnalysis</c> metadata
///         references — which loads <c>Microsoft.CodeAnalysis.*</c> into processes
///         that never compile a single plugin (<c>harbor providers</c> on a machine
///         with no plugin dirs). Behind this descriptor an empty plugin source
///         compiles nothing and loads nothing; a cache hit in the wrapping
///         <see cref="CachingCompiler" /> never touches the inner compiler at all,
///         so the healthy path is unchanged.
///     </para>
///     <para>
///         The descriptor itself references no compiler type: the factory lambda
///         lives at the composition site, so JIT-compiling this class cannot pull
///         Roslyn in either.
///     </para>
/// </remarks>
public sealed class DeferredPluginCompiler : IPluginCompiler
{
    private readonly Lazy<IPluginCompiler> _inner;

    /// <summary>
    ///     Construct a deferred compiler over the given factory.
    /// </summary>
    /// <param name="factory">
    ///     Builds the real compiler on first use. Invoked at most once
    ///     (<see cref="LazyThreadSafetyMode.ExecutionAndPublication" />).
    /// </param>
    public DeferredPluginCompiler(Func<IPluginCompiler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inner = new Lazy<IPluginCompiler>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    ///     True once the inner compiler has been built. Test seam for the
    ///     absence matrix: a plugin-less load must never flip this.
    /// </summary>
    public bool IsValueCreated => _inner.IsValueCreated;

    /// <inheritdoc />
    public Task<Result<CompiledPluginAssembly>> CompileAsync(PluginScript script, CancellationToken ct = default)
        => _inner.Value.CompileAsync(script, ct);
}
