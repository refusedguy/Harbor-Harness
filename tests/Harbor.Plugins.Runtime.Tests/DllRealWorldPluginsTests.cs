using System.Reflection;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Real-world plugin corpus (issue #422, slice 2): the same DI, async and
///     multi-facet fixtures as slice 1, but loaded as prebuilt <b>DLL files from
///     disk</b> — the path a shipped/nupkg plugin takes — instead of through the
///     CS-source <see cref="CsPluginLoader" /> facade. Each test compiles the fixture
///     once with <see cref="RoslynPluginCompiler" />, persists the emitted PE image
///     as <c>*.dll</c>, loads it back via
///     <see cref="CollectiblePluginLoadContext.LoadFromPluginPath" /> (the exact call
///     <see cref="CachingCompiler" /> makes on a cache hit), then instantiates with
///     <see cref="ReflectionPluginInstantiator" /> and registers with
///     <see cref="PluginRegistrar" />. Every test asserts an observable effect
///     (executed tool result, registered panel id, registered agent name), not
///     merely "no exception".
/// </summary>
public sealed class DllRealWorldPluginsTests
{
    /// <summary>
    ///     The DI fixture survives the DLL round-trip: the tool executes with its
    ///     constructor-injected factory, and the factory the plugin captured in
    ///     <c>Initialize</c> is the host singleton by identity.
    /// </summary>
    [Test]
    public async Task DllDiTool_InstantiateRegister_ExecutesWithHostSingleton()
    {
        (FakePluginLoadHost host, IReadOnlyList<LoadedPlugin> loaded) =
            await LoadDllAsync("DiToolPlugin.cs", "rw-dll-di").ConfigureAwait(false);
        await Assert.That(loaded.Count).IsEqualTo(1);
        await Assert.That(loaded[0].Name).IsEqualTo("rw-di");
        await Assert.That(loaded[0].LoadedFromCache).IsTrue();

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_di");

        object? captured = loaded[0].Instance.GetType()
            .GetField("CapturedLoggerFactory", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        await Assert.That(ReferenceEquals(host.LoggerFactory, captured)).IsTrue();

        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await ExecuteThroughSandboxAsync(tool, doc.RootElement).ConfigureAwait(false);
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("di-ok");
    }

    /// <summary>
    ///     The async fixture's long-payload tool and the failing-path tool both work
    ///     after the DLL round-trip: ~8KB of awaited output, and an error result
    ///     rather than an exception.
    /// </summary>
    [Test]
    public async Task DllAsyncPayload_InstantiateRegister_ReturnsPayloadAndErrorResult()
    {
        (FakePluginLoadHost host, _) =
            await LoadDllAsync("AsyncPayloadPlugin.cs", "rw-dll-async").ConfigureAwait(false);

        ITool asyncTool = host.RegisteredTools.Single(t => t.Name.Value == "rw_async");
        using var doc = JsonDocument.Parse("{}");
        ToolResult payload = await ExecuteThroughSandboxAsync(asyncTool, doc.RootElement).ConfigureAwait(false);
        await Assert.That(payload.IsError).IsFalse();
        await Assert.That(payload.Output.Length).IsGreaterThanOrEqualTo(8000);

        ITool failingTool = host.RegisteredTools.Single(t => t.Name.Value == "rw_failing");
        ToolResult failure = await ExecuteThroughSandboxAsync(failingTool, doc.RootElement).ConfigureAwait(false);
        await Assert.That(failure.IsError).IsTrue();
        await Assert.That(failure.Output).Contains("rw-boom");
    }

    /// <summary>
    ///     The three-facet fixture registers all three halves after the DLL
    ///     round-trip: its tool executes, its panel provider lands on the host, and
    ///     its agent definition lands on the host.
    /// </summary>
    [Test]
    public async Task DllMultiFacet_InstantiateRegister_RegistersToolPanelAndAgent()
    {
        (FakePluginLoadHost host, IReadOnlyList<LoadedPlugin> loaded) =
            await LoadDllAsync("MultiFacetPanelPlugin.cs", "rw-dll-multi").ConfigureAwait(false);
        await Assert.That(loaded.Count).IsEqualTo(1);
        await Assert.That(loaded[0].Name).IsEqualTo("rw-multi");

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_panel_tool");
        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await ExecuteThroughSandboxAsync(tool, doc.RootElement).ConfigureAwait(false);
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("panel-tool-ok");

        await Assert.That(host.RegisteredPanelProviders.Any(p => p.Id == "rw-panel")).IsTrue();
        await Assert.That(host.RegisteredAgents.Any(a => a.Name.Value == "rw-agent")).IsTrue();
    }

    /// <summary>
    ///     The production <see cref="CachingCompiler" /> serves a real-world fixture
    ///     from its on-disk DLL cache: the second compile reports
    ///     <c>FromCache=true</c> with no in-memory bytes (proof it came from disk),
    ///     and the cache-hit assembly instantiates, registers, and executes.
    /// </summary>
    [Test]
    public async Task DllCacheHit_ProductionCachingCompiler_LoadsFromDiskAndRegisters()
    {
        string repoRoot = LocateRepoRoot();
        string source = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tests", "Plugins", "RealWorld", "DiToolPlugin.cs")).ConfigureAwait(false);

        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "DLLCACHE").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var compiler = new CachingCompiler(
            new RoslynPluginCompiler(references),
            fixture.CacheDir,
            NullLogger<CachingCompiler>.Instance);
        var script = new PluginScript(
            Path.Combine(fixture.PluginsDir, "DiToolPlugin.cs"), source);

        var first = await compiler.CompileAsync(script).ConfigureAwait(false);
        if (first.IsFailure)
            throw new InvalidOperationException($"First compile failed: {first.Error}");
        await Assert.That(first.Value.FromCache).IsFalse();

        var second = await compiler.CompileAsync(script).ConfigureAwait(false);
        if (second.IsFailure)
            throw new InvalidOperationException($"Cache-hit compile failed: {second.Error}");
        await Assert.That(second.Value.FromCache).IsTrue();
        await Assert.That(second.Value.AssemblyBytes).IsNull();

        var host = new FakePluginLoadHost();
        var instantiator = new ReflectionPluginInstantiator();
        var instantiated = instantiator.Instantiate(second.Value);
        if (instantiated.IsFailure)
            throw new InvalidOperationException($"Instantiate from cache-hit DLL failed: {instantiated.Error}");

        var registrar = new PluginRegistrar(
            fixture.PluginsDir,
            NullLogger<PluginRegistrar>.Instance,
            NullLoggerFactory.Instance);
        foreach (var plugin in instantiated.Value)
        {
            var registered = registrar.Register(plugin, host);
            if (registered.IsFailure)
                throw new InvalidOperationException($"Register from cache-hit DLL failed: {registered.Error}");
        }

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_di");
        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await ExecuteThroughSandboxAsync(tool, doc.RootElement).ConfigureAwait(false);
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("di-ok");
    }

    /// <summary>
    ///     Compile a corpus fixture, persist its PE image as a <c>*.dll</c> file, load
    ///     that file back through the production cache-hit call
    ///     (<see cref="CollectiblePluginLoadContext.LoadFromPluginPath" />), and run
    ///     it through the real instantiation + registration layers. The temp
    ///     directory is disposed on return; the loaded assemblies live in
    ///     collectible contexts and stay usable.
    /// </summary>
    private static async Task<(FakePluginLoadHost Host, IReadOnlyList<LoadedPlugin> Loaded)> LoadDllAsync(
        string fileName, string dllName)
    {
        string repoRoot = LocateRepoRoot();
        string source = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tests", "Plugins", "RealWorld", fileName)).ConfigureAwait(false);

        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "DLL").ConfigureAwait(false);

        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var roslyn = new RoslynPluginCompiler(references);
        var script = new PluginScript(Path.Combine(fixture.PluginsDir, fileName), source);
        var compiled = await roslyn.CompileAsync(script).ConfigureAwait(false);
        if (compiled.IsFailure)
            throw new InvalidOperationException($"Fixture {fileName} failed to compile: {compiled.Error}");
        if (compiled.Value.AssemblyBytes is null)
            throw new InvalidOperationException($"Fixture {fileName} compiled without PE bytes.");

        // The DLL hop: what a prebuilt/shipped plugin looks like on disk.
        string dllDir = Path.Combine(fixture.HarborDir, "dll");
        Directory.CreateDirectory(dllDir);
        string dllPath = Path.Combine(dllDir, dllName + ".dll");
        await File.WriteAllBytesAsync(dllPath, compiled.Value.AssemblyBytes).ConfigureAwait(false);

        var alc = CollectiblePluginLoadContext.ForScript(script);
        var dllAssembly = alc.LoadFromPluginPath(dllPath);
        var fromDll = new CompiledPluginAssembly(
            dllAssembly,
            script.Hash,
            dllPath,
            AssemblyBytes: null,
            FromCache: true,
            script.DeclaredCapabilities);

        var instantiator = new ReflectionPluginInstantiator();
        var instantiated = instantiator.Instantiate(fromDll);
        if (instantiated.IsFailure)
            throw new InvalidOperationException($"Fixture {fileName} failed to instantiate from DLL: {instantiated.Error}");

        var host = new FakePluginLoadHost();
        var registrar = new PluginRegistrar(
            fixture.PluginsDir,
            NullLogger<PluginRegistrar>.Instance,
            NullLoggerFactory.Instance);
        var loaded = new List<LoadedPlugin>(instantiated.Value.Count);
        foreach (var plugin in instantiated.Value)
        {
            var registered = registrar.Register(plugin, host);
            if (registered.IsFailure)
                throw new InvalidOperationException($"Fixture {fileName} failed to register from DLL: {registered.Error}");
            loaded.Add(new LoadedPlugin(
                plugin.Instance,
                plugin.Name,
                plugin.Version,
                plugin.PluginType,
                plugin.SourcePath,
                plugin.SourceHash,
                LoadedFromCache: true,
                plugin.DeclaredCapabilities));
        }

        return (host, loaded);
    }

    /// <summary>
    ///     Execute a registered tool through its sandbox wrapper, tolerating a transient
    ///     <c>[sandbox:memory]</c> block with a bounded retry. The sandbox samples the
    ///     <b>process-wide</b> allocation counter, so an unrelated burst on another thread
    ///     (a sibling test's Roslyn compile under TUnit parallelism) can exceed the 10 MB
    ///     budget inside the await window and convert a healthy result into an error
    ///     (issue #1050). Only that signature retries; any other error returns
    ///     immediately, and a persistent memory block still fails the test.
    /// </summary>
    private static async Task<ToolResult> ExecuteThroughSandboxAsync(ITool tool, JsonElement args)
    {
        ToolResult result = await tool.ExecuteAsync(args, MakeContext()).ConfigureAwait(false);
        for (int attempt = 1;
            result.IsError
                && result.Output.Contains("[sandbox:memory]", StringComparison.Ordinal)
                && attempt < 5;
            attempt++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            result = await tool.ExecuteAsync(args, MakeContext()).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    ///     Build a <see cref="ToolContext" /> mirroring production: the agent loop passes
    ///     no service provider, so the tools under test must never need one.
    /// </summary>
    private static ToolContext MakeContext() => new(
        "sess-rw-dll",
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"),
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Deny, false)));

    /// <summary>
    ///     Walk up from the test binaries to the repository root (the directory
    ///     that contains <c>tests/Plugins/RealWorld</c>).
    /// </summary>
    private static string LocateRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "tests", "Plugins", "RealWorld");
            if (Directory.Exists(probe))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root with tests/Plugins/RealWorld not found above {AppContext.BaseDirectory}.");
    }
}
