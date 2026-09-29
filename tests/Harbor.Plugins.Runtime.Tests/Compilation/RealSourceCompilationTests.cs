using CSharpFunctionalExtensions;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Compilation;

/// <summary>
///     Integration tests over REAL plugin source material (sprint Testing
///     Strategy Z.4): the shipped <c>samples/plugins-cs/HelloWorldPlugin.cs</c>,
///     the same source with an injected deliberate error (missing using), and a
///     circular-dependency source. No hand-waved "should work" — every path
///     asserts concrete compiler behaviour: cache hits, line-numbered error
///     reporting, graceful failure.
/// </summary>
public sealed class RealSourceCompilationTests
{
    private const string SampleRelativePath = "samples/plugins-cs/HelloWorldPlugin.cs";

    /// <summary>
    ///     The REAL shipped hello-world sample compiles through
    ///     <see cref="CachingCompiler" /> and the SECOND compile of the identical
    ///     source is served from the cache — the path every user plugin takes on
    ///     its second load.
    /// </summary>
    [Test]
    public async Task CachingCompiler_RealHelloWorldSample_SecondCompileHitsCache()
    {
        string repoRoot = LocateRepoRoot();
        var scriptLoad = await PluginScript.LoadAsync(Path.Combine(repoRoot, SampleRelativePath))
            .ConfigureAwait(false);
        await Assert.That(scriptLoad.IsSuccess).IsTrue();

        using var fixture = await PluginTestFixture.CreateAsync("real-src-cache").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var compiler = new CachingCompiler(
            new RoslynPluginCompiler(new PluginAssemblyReferences(NullLogger<PluginAssemblyReferences>.Instance)),
            fixture.CacheDir,
            NullLogger<CachingCompiler>.Instance);

        Result<CompiledPluginAssembly> first = await compiler.CompileAsync(scriptLoad.Value).ConfigureAwait(false);

        // Surface the real diagnostics on CI drift instead of a bare assert. See
        // CompilationLayerTests.Test1 for why the `IsEqualTo(string.Empty)` form
        // this replaced is gone with the hand-rolled result (#561).
        await Assert.That(first.IsSuccess).IsTrue()
            .Because(first.IsFailure ? $"compilation failed: {first.Error}" : "the shipped sample should compile");
        await Assert.That(first.Value.FromCache).IsFalse();

        // The compiled assembly really contains the shipped plugin type.
        var pluginType = first.Value.Assembly.GetType(
            "Harbor.Sample.HelloWorld.HelloWorldPlugin", throwOnError: false);
        await Assert.That(pluginType).IsNotNull();

        Result<CompiledPluginAssembly> second = await compiler.CompileAsync(scriptLoad.Value).ConfigureAwait(false);
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.FromCache).IsTrue();

        // A .dll artifact was persisted under the cache directory.
        string[] cacheFiles = Directory.GetFiles(fixture.CacheDir, "*.dll");
        await Assert.That(cacheFiles.Length).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    ///     Injecting a deliberate error into the real sample (removing a
    ///     required using) must fail the compile AND report the error with the
    ///     source LINE NUMBER — both in the Roslyn diagnostics and in the
    ///     human-readable error string the host surfaces to users.
    /// </summary>
    [Test]
    public async Task MissingUsing_InRealSource_ReportsErrorWithLineNumber()
    {
        string repoRoot = LocateRepoRoot();
        string source = await File.ReadAllTextAsync(Path.Combine(repoRoot, SampleRelativePath))
            .ConfigureAwait(false);

        // Deliberate error injection: point the plugin class at a contract that
        // does not exist. (Removing a `using` is NOT an error here — the
        // compiler auto-injects the Harbor contract namespaces as global
        // usings, see RoslynPluginCompiler.ImplicitUsingNamespaces.)
        string brokenSource = source.Replace(
            "public sealed class HelloWorldPlugin : IToolPlugin",
            "public sealed class HelloWorldPlugin : IMissingPluginContract");
        await Assert.That(brokenSource).Contains("IMissingPluginContract");
        var script = new PluginScript("broken-hello-world.cs", brokenSource);

        var compiler = new RoslynPluginCompiler(
            new PluginAssemblyReferences(NullLogger<PluginAssemblyReferences>.Instance));
        Result<CompiledPluginAssembly> result = await compiler.CompileAsync(script).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();

        // The error string surfaced to users carries the diagnostic id AND the
        // 1-based line of the class declaration — the same information the deleted
        // structured `Diagnostic` member carried, read back out of the rendered
        // form, which is the only channel there is now that the member is gone (#561).
        int expectedLine = FirstIdentifierLine(brokenSource, "IMissingPluginContract");
        await Assert.That(result.Error).Contains("CS0246");
        await Assert.That(result.Error).Contains($"broken-hello-world.cs({expectedLine},");
    }

    /// <summary>
    ///     A circular base-class dependency (A : B, B : A) must fail GRACEFULLY:
    ///     a clean failure result carrying the CS0146 diagnostic — no exception
    ///     escaping the compiler.
    /// </summary>
    [Test]
    public async Task CircularBaseClassDependency_FailsGracefullyWithDiagnostic()
    {
        const string circularSource = """
            using Harbor.Abstractions.Plugins;

            namespace Harbor.Sample.Circular;

            // Not sealed: a sealed base would pre-empt the cycle with CS0509 —
            // we want the genuine circular-dependency diagnostic (CS0146).
            public class CircularPluginA : CircularPluginB
            {
                public string Name => "circular-a";
                public Version Version => new(1, 0, 0);
                public string Description => "circular";
                public void Initialize(PluginContext context) { }
            }

            public class CircularPluginB : CircularPluginA
            {
                public string Name => "circular-b";
                public Version Version => new(1, 0, 0);
                public string Description => "circular";
                public void Initialize(PluginContext context) { }
            }
            """;
        var script = new PluginScript("circular.cs", circularSource);

        var compiler = new RoslynPluginCompiler(
            new PluginAssemblyReferences(NullLogger<PluginAssemblyReferences>.Instance));
        Result<CompiledPluginAssembly> result = await compiler.CompileAsync(script).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();

        // Message text is locale-dependent, so only the id and the
        // file(line,column) position are asserted. Since #561 the diagnostics live
        // in the error string the host surfaces, so the rendered form is what is
        // checked — there is no structured member left to read them from.
        await Assert.That(result.Error).Contains("CS0146");

        // ...and the position is one of the two class declarations, which is what
        // makes the failure actionable. The cycle fires once per class, so either
        // line satisfies it; the previous version of this test never claimed which.
        int lineA = FirstIdentifierLine(circularSource, "class CircularPluginA");
        int lineB = FirstIdentifierLine(circularSource, "class CircularPluginB");
        bool reportsAPosition =
            result.Error.Contains($"circular.cs({lineA},", StringComparison.Ordinal)
            || result.Error.Contains($"circular.cs({lineB},", StringComparison.Ordinal);

        await Assert.That(reportsAPosition).IsTrue()
            .Because(
                $"the CS0146 cycle must be reported at one of the class declarations (lines {lineA} "
                + $"or {lineB}); the error was: {result.Error}");
    }

    /// <summary>1-based line of the first occurrence of <paramref name="identifier" /> in the source.</summary>
    private static int FirstIdentifierLine(string source, string identifier)
    {
        string[] lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(identifier, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"identifier '{identifier}' not found in source");
    }

    /// <summary>Walk up from the test binaries to the repo root (contains samples/plugins-cs).</summary>
    private static string LocateRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "samples", "plugins-cs");
            if (Directory.Exists(probe))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root with samples/plugins-cs not found above {AppContext.BaseDirectory}.");
    }
}
