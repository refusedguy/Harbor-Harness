using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Compilation;
/// <summary>
///     Tests for the Compilation layer: <see cref="RoslynPluginCompiler" /> and
///     <see cref="CachingCompiler" />.
/// </summary>
public sealed class CompilationLayerTests
{
    /// <summary>
    ///     Test 1 — <see cref="RoslynPluginCompiler" /> compiles a valid hello-world
    ///     source into an <see cref="CompiledPluginAssembly" /> whose assembly contains
    ///     at least one IPlugin implementation.
    /// </summary>
    [Test]
    public async Task RoslynCompiler_ValidSource_CompilesToAssembly()
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var compiler = new RoslynPluginCompiler(references);
        var script = new PluginScript("test.cs", SamplePluginSource.HelloWorld("Comp1"));

        var result = await compiler.CompileAsync(script).ConfigureAwait(false);

        // Surface the actual error message if compilation fails — a bare
        // `IsTrue()` gives "Expected to be true but found False" with no
        // context, which makes Roslyn reference drift impossible to diagnose from
        // CI logs. #561 also retired the `IsEqualTo(string.Empty)` trick this used
        // to rely on: it only "worked" because a failure carried a non-empty error,
        // which is exactly the ambiguity the hand-rolled result had and Result<T>
        // does not.
        await Assert.That(result.IsSuccess).IsTrue()
            .Because(result.IsFailure ? $"compilation failed: {result.Error}" : "the sample should compile");
        await Assert.That(result.Value.Assembly).IsNotNull();
        await Assert.That(result.Value.FromCache).IsFalse();
    }

    /// <summary>
    ///     Test 2 — <see cref="RoslynPluginCompiler" /> returns a failure result with
    ///     diagnostics when the source has a syntax error.
    /// </summary>
    [Test]
    public async Task RoslynCompiler_SyntaxError_ReturnsFailureWithDiagnostics()
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var compiler = new RoslynPluginCompiler(references);
        var script = new PluginScript("broken.cs", SamplePluginSource.Broken());

        var result = await compiler.CompileAsync(script).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("compilation failed");

        // Since #561 there is no structured `Diagnostics` member: it had no
        // production reader, and the diagnostics are rendered into this error
        // string one per line as "  [Severity] path(line,col): CSxxxx — message".
        // Asserting the rendered form is asserting what a plugin author actually
        // sees, rather than a channel only tests read.
        await Assert.That(result.Error).Contains("[Error]");
        await Assert.That(result.Error).Contains("broken.cs(");
    }

    /// <summary>
    ///     Test 3 — <see cref="CachingCompiler" /> writes a .dll to the cache directory
    ///     on first compile and reports <c>FromCache=false</c>; on second compile with
    ///     the same source, it loads the cached .dll and reports <c>FromCache=true</c>.
    /// </summary>
    [Test]
    public async Task CachingCompiler_SecondCall_HitsCacheAndSetsFromCache()
    {
        using var fixture = await PluginTestFixture.CreateAsync("caching").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var inner = new RoslynPluginCompiler(references);
        var caching = new CachingCompiler(
            inner, fixture.CacheDir, NullLogger<CachingCompiler>.Instance);

        var script = new PluginScript("test.cs", SamplePluginSource.HelloWorld("Cache1"));

        var first = await caching.CompileAsync(script).ConfigureAwait(false);
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(first.Value.FromCache).IsFalse();

        // Cache file should exist now.
        string[] cacheFiles = Directory.GetFiles(fixture.CacheDir, "*.dll");
        await Assert.That(cacheFiles.Length).IsGreaterThanOrEqualTo(1);

        // Second compile — same hash → cache hit.
        var second = await caching.CompileAsync(script).ConfigureAwait(false);
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.FromCache).IsTrue();
    }

    /// <summary>
    ///     Test 4 — <see cref="CachingCompiler" /> delegates to the inner compiler when
    ///     the cache directory is empty (cold-start path).
    /// </summary>
    [Test]
    public async Task CachingCompiler_EmptyCache_DelegatesToInner()
    {
        using var fixture = await PluginTestFixture.CreateAsync("caching-cold").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var inner = new RoslynPluginCompiler(references);
        var caching = new CachingCompiler(
            inner, fixture.CacheDir, NullLogger<CachingCompiler>.Instance);

        var script = new PluginScript("cold.cs", SamplePluginSource.HelloWorld("Cold1"));
        var result = await caching.CompileAsync(script).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.FromCache).IsFalse();
    }
}
