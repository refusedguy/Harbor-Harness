using System.Reflection;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Real-world CS-source plugin corpus (issue #422, slice 1): the DI, async and
///     multi-facet fixtures under <c>tests/Plugins/RealWorld/</c> compile and load
///     through the production <see cref="CsPluginLoader" /> pipeline. Every test asserts
///     an observable effect (registered tool name, executed tool result, registered
///     panel id, registered agent name), not merely "no exception".
/// </summary>
public sealed class RealWorldPluginsTests
{
    /// <summary>
    ///     The DI fixture's tool executes with a constructor-injected factory, and the
    ///     factory the plugin captured in <c>Initialize</c> is the host singleton by
    ///     identity — dependencies come from the host container, never from the
    ///     execution context (which carries no container by design, #470).
    /// </summary>
    [Test]
    public async Task DiTool_LoadsFromHostContainer_ExecutesWithCtorInjectedFactory()
    {
        (FakePluginLoadHost host, IReadOnlyList<CompiledPlugin> loaded) =
            await LoadAsync("DiToolPlugin.cs").ConfigureAwait(false);
        await Assert.That(loaded.Count).IsEqualTo(1);
        await Assert.That(loaded[0].Name).IsEqualTo("rw-di");

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_di");

        object? captured = loaded[0].Instance.GetType()
            .GetField("CapturedLoggerFactory", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        await Assert.That(ReferenceEquals(host.LoggerFactory, captured)).IsTrue();

        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await tool.ExecuteAsync(doc.RootElement, MakeContext()).ConfigureAwait(false);
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("di-ok");
    }

    /// <summary>
    ///     The async fixture's tool awaits real async work and returns an ~8KB payload.
    ///     Execution goes through the registered sandbox wrapper.
    /// </summary>
    [Test]
    public async Task AsyncTool_AwaitsWork_ReturnsLongPayload()
    {
        (FakePluginLoadHost host, _) = await LoadAsync("AsyncPayloadPlugin.cs").ConfigureAwait(false);

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_async");
        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await tool.ExecuteAsync(doc.RootElement, MakeContext()).ConfigureAwait(false);
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output.Length).IsGreaterThanOrEqualTo(8000);
    }

    /// <summary>
    ///     The failing path surfaces as an error result, never as an exception.
    /// </summary>
    [Test]
    public async Task FailingTool_ReturnsErrorResult_DoesNotThrow()
    {
        (FakePluginLoadHost host, _) = await LoadAsync("AsyncPayloadPlugin.cs").ConfigureAwait(false);

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_failing");
        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await tool.ExecuteAsync(doc.RootElement, MakeContext()).ConfigureAwait(false);
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("rw-boom");
    }

    /// <summary>
    ///     One plugin with three facets: its tool executes, its panel provider lands on
    ///     the host, and its agent definition lands on the host.
    /// </summary>
    [Test]
    public async Task MultiFacet_RegistersToolPanelAndAgent()
    {
        (FakePluginLoadHost host, IReadOnlyList<CompiledPlugin> loaded) =
            await LoadAsync("MultiFacetPanelPlugin.cs").ConfigureAwait(false);
        await Assert.That(loaded.Count).IsEqualTo(1);
        await Assert.That(loaded[0].Name).IsEqualTo("rw-multi");

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "rw_panel_tool");
        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await tool.ExecuteAsync(doc.RootElement, MakeContext()).ConfigureAwait(false);
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("panel-tool-ok");

        await Assert.That(host.RegisteredPanelProviders.Any(p => p.Id == "rw-panel")).IsTrue();
        await Assert.That(host.RegisteredAgents.Any(a => a.Name.Value == "rw-agent")).IsTrue();
    }

    /// <summary>
    ///     Editing a fixture source changes its SHA-256, so the second load re-compiles
    ///     instead of hitting the cache.
    /// </summary>
    [Test]
    public async Task CacheInvalidation_SourceEdit_Recompiles()
    {
        string repoRoot = LocateRepoRoot();
        string source = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tests", "Plugins", "RealWorld", "DiToolPlugin.cs")).ConfigureAwait(false);

        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "RWCI").ConfigureAwait(false);
        await fixture.WritePluginAsync(source, "DiToolPlugin.cs").ConfigureAwait(false);

        var host1 = new FakePluginLoadHost();
        var loader1 = new CsPluginLoader(
            host1,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);
        var first = await loader1.DiscoverAndLoadAsync().ConfigureAwait(false);
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(first.Value.Count).IsEqualTo(1);
        await Assert.That(first.Value[0].LoadedFromCache).IsFalse();

        await File.AppendAllTextAsync(
            Path.Combine(fixture.PluginsDir, "DiToolPlugin.cs"), "\n// cache-buster\n").ConfigureAwait(false);

        var host2 = new FakePluginLoadHost();
        var loader2 = new CsPluginLoader(
            host2,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);
        var second = await loader2.DiscoverAndLoadAsync().ConfigureAwait(false);
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.Count).IsEqualTo(1);
        await Assert.That(second.Value[0].LoadedFromCache).IsFalse();
        await Assert.That(host2.RegisteredTools.Any(t => t.Name.Value == "rw_di")).IsTrue();
    }

    /// <summary>
    ///     Read a corpus fixture into a fresh temp-HOME fixture and load it through the
    ///     production loader. The temp directory is disposed on return; the loaded
    ///     assemblies live in collectible contexts and stay usable.
    /// </summary>
    private static async Task<(FakePluginLoadHost Host, IReadOnlyList<CompiledPlugin> Loaded)> LoadAsync(string fileName)
    {
        string repoRoot = LocateRepoRoot();
        string source = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tests", "Plugins", "RealWorld", fileName)).ConfigureAwait(false);

        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "RW").ConfigureAwait(false);
        await fixture.WritePluginAsync(source, fileName).ConfigureAwait(false);

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);

        var result = await loader.DiscoverAndLoadAsync().ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Fixture {fileName} failed to load: {result.Error}");
        return (host, result.Value);
    }

    /// <summary>
    ///     Build a <see cref="ToolContext" /> mirroring production: the agent loop passes
    ///     no service provider, so the tools under test must never need one.
    /// </summary>
    private static ToolContext MakeContext() => new(
        "sess-rw",
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
