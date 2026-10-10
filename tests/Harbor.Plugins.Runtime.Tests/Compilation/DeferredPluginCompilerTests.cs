using CSharpFunctionalExtensions;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;

namespace Harbor.Plugins.Runtime.Tests.Compilation;

/// <summary>
///     Tests for <see cref="DeferredPluginCompiler" /> (#1055 slice 1): the inner
///     compiler builds on first use, exactly once, and results delegate to it.
///     A counting fake stands in for Roslyn so these tests never pay the
///     compilation cost and never load <c>Microsoft.CodeAnalysis</c>.
/// </summary>
public sealed class DeferredPluginCompilerTests
{
    /// <summary>
    ///     Constructing the descriptor must not invoke the factory — that is the
    ///     whole point: composing the pipeline stays free of Roslyn.
    /// </summary>
    [Test]
    public async Task Construction_DoesNotBuildInner()
    {
        int calls = 0;
        var deferred = new DeferredPluginCompiler(() =>
        {
            calls++;
            return new CountingCompiler();
        });

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(deferred.IsValueCreated).IsFalse();
    }

    /// <summary>
    ///     The first <c>CompileAsync</c> builds the inner compiler and delegates
    ///     the call, surfacing its result untouched.
    /// </summary>
    [Test]
    public async Task FirstCompile_BuildsInnerOnce_DelegatesResult()
    {
        int calls = 0;
        var deferred = new DeferredPluginCompiler(() =>
        {
            calls++;
            return new CountingCompiler();
        });
        var script = new PluginScript("deferred.cs", "// healthy");

        var result = await deferred.CompileAsync(script).ConfigureAwait(false);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(deferred.IsValueCreated).IsTrue();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.SourcePath).IsEqualTo("deferred.cs");
    }

    /// <summary>
    ///     The inner compiler is a singleton behind the descriptor: later calls
    ///     reuse it instead of rebuilding.
    /// </summary>
    [Test]
    public async Task SecondCompile_ReusesInner()
    {
        int calls = 0;
        var deferred = new DeferredPluginCompiler(() =>
        {
            calls++;
            return new CountingCompiler();
        });
        var script = new PluginScript("deferred.cs", "// healthy");

        await deferred.CompileAsync(script).ConfigureAwait(false);
        await deferred.CompileAsync(script).ConfigureAwait(false);

        await Assert.That(calls).IsEqualTo(1);
    }

    private sealed class CountingCompiler : IPluginCompiler
    {
        public Task<Result<CompiledPluginAssembly>> CompileAsync(PluginScript script, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new CompiledPluginAssembly(
                typeof(CountingCompiler).Assembly,
                script.Hash,
                script.Path)));
    }
}
