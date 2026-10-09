using System;
using System.Threading;
using System.Threading.Tasks;
using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     <see cref="IPluginCompiler" /> that defers construction of the inner
///     compiler until the first <see cref="CompileAsync" /> call (#1055, slice 2).
///     The default inner stack (reference snapshot + Roslyn) JITs
///     <c>Microsoft.CodeAnalysis</c> and walks the <c>AppDomain</c> at
///     construction time — composing it eagerly taxes every CLI start (the
///     43→94MB gap on <c>providers</c>), including starts that load zero plugins.
/// </summary>
public sealed class LazyPluginCompiler : IPluginCompiler
{
    private readonly Lazy<IPluginCompiler> _inner;

    /// <summary>
    ///     Construct a lazy compiler over the supplied factory.
    /// </summary>
    /// <param name="factory">
    ///     Inner compiler factory, invoked at most once (thread-safe,
    ///     first-writer-wins). Keep Roslyn-touching construction behind it —
    ///     that is what keeps the composition path free of the JIT cost.
    /// </param>
    public LazyPluginCompiler(Func<IPluginCompiler> factory)
    {
        _inner = new Lazy<IPluginCompiler>(
            factory ?? throw new ArgumentNullException(nameof(factory)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public Task<Result<CompiledPluginAssembly>> CompileAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));

        // Same idiom as ProviderRegistry.Instantiate: force the lazy factory
        // through the library, so an init failure is a per-plugin Result the
        // host isolates (ContinueOnError) rather than a throw that aborts the run.
        return Result.Success(_inner)
            .MapTry(static lazy => lazy.Value, static ex => $"Lazy plugin compiler init failed: {ex.Message}")
            .Bind(inner => inner.CompileAsync(script, ct));
    }
}
