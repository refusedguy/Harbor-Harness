using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Runtime;
using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Abstractions.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Tests for <see cref="CsPluginLoader" /> — Roslyn-based CS-source plugin loading,
///     compilation diagnostics, and on-disk caching.
/// </summary>
public sealed class CsPluginLoaderTests
{
    /// <summary>
    ///     Test 1 — A well-formed hello-world .cs file compiles, instantiates, and
    ///     registers its <c>hello</c> tool with the host.
    /// </summary>
    [Test]
    public async Task CompileAndLoad_ValidHelloWorld_RegistersHelloTool()
    {
        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "A").ConfigureAwait(false);
        await fixture.WritePluginAsync(HelloWorldSource("A")).ConfigureAwait(false);

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            harborDir: fixture.HarborDir);

        var result = await loader.DiscoverAndLoadAsync().ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("hello-world-a");
        await Assert.That(host.RegisteredTools).HasCount(1);
        await Assert.That(host.RegisteredTools[0].Name.Value).IsEqualTo("hello_a");
    }

    /// <summary>
    ///     Test 2 — A .cs file with a syntax error fails to compile and the loader returns
    ///     a <see cref="PluginCompilationResult" /> with <see cref="PluginCompilationResult.IsFailure" />
    ///     = true and a non-empty error message.
    /// </summary>
    [Test]
    public async Task CompileAndLoad_SyntaxError_ReturnsFailureResult()
    {
        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "B").ConfigureAwait(false);
        var script = new PluginScript(
            path: Path.Combine(fixture.PluginsDir, "broken.cs"),
            source: """
                // This file is intentionally broken.
                public sealed class Broken {
                    public void M() {
                        int x = ; // syntax error — missing expression
                    }
                }
                """);

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            harborDir: fixture.HarborDir);

        var result = await loader.CompileAndLoadAsync(script).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("compilation failed");
    }

    /// <summary>
    ///     Test 3 — After the first successful load, the compiled assembly is cached to
    ///     <c>~/.harbor/plugins/cache/{hash}.dll</c>. A second load with the same source
    ///     returns a <see cref="CompiledPlugin" /> with <see cref="CompiledPlugin.LoadedFromCache" />
    ///     = true and skips Roslyn.
    /// </summary>
    [Test]
    public async Task CompileAndLoad_SecondCall_HitsCacheAndSetsLoadedFromCache()
    {
        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "C").ConfigureAwait(false);
        await fixture.WritePluginAsync(HelloWorldSource("C")).ConfigureAwait(false);

        var host1 = new FakePluginLoadHost();
        var loader1 = new CsPluginLoader(
            host1,
            NullLogger<CsPluginLoader>.Instance,
            harborDir: fixture.HarborDir);

        var first = await loader1.DiscoverAndLoadAsync().ConfigureAwait(false);
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(first.Value.Count).IsEqualTo(1);
        await Assert.That(first.Value[0].LoadedFromCache).IsFalse();

        // Verify cache file was written.
        string cacheDir = Path.Combine(fixture.HarborDir, "plugins", "cache");
        var cacheFiles = Directory.Exists(cacheDir)
            ? Directory.GetFiles(cacheDir, "*.dll")
            : Array.Empty<string>();
        await Assert.That(cacheFiles.Length).IsGreaterThanOrEqualTo(1);

        // Second load — same source hash should hit the cache.
        var host2 = new FakePluginLoadHost();
        var loader2 = new CsPluginLoader(
            host2,
            NullLogger<CsPluginLoader>.Instance,
            harborDir: fixture.HarborDir);

        var second = await loader2.DiscoverAndLoadAsync().ConfigureAwait(false);
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.Count).IsEqualTo(1);
        await Assert.That(second.Value[0].LoadedFromCache).IsTrue();
        await Assert.That(second.Value[0].Name).IsEqualTo("hello-world-c");
    }

    /// <summary>
    ///     Generate a hello-world CS-source plugin with the given unique suffix. The
    ///     suffix is appended to class and tool names so different test runs don't
    ///     collide in the shared AppDomain type system.
    /// </summary>
    private static string HelloWorldSource(string suffix) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using CSharpFunctionalExtensions;
        using Harbor.Abstractions.Models;
        using Harbor.Abstractions.Models.Identifiers;
        using Harbor.Abstractions.Plugins;
        using Harbor.Abstractions.Tools;
        using Microsoft.Extensions.Logging;

        public sealed class HelloPlugin{{suffix}} : IToolPlugin
        {
            public string Name => "hello-world-{{suffix.ToLowerInvariant()}}";
            public Version Version => new(1, 0, 0);
            public Version RequiredHarborVersion => new(0, 4, 0);
            public string Description => "Test plugin {{suffix}}";

            public void Initialize(PluginContext context)
            {
                context.CreateLogger<HelloPlugin{{suffix}}>().LogInformation("{{suffix}} initialized");
            }

            public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<HelloTool{{suffix}}>();

            public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        public sealed class HelloTool{{suffix}} : ITool
        {
            public ToolName Name => ToolName.Create("hello_{{suffix.ToLowerInvariant()}}");
            public string DisplayName => "Hello {{suffix}}";
            public string Description => "Returns a greeting";
            public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
            public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
            public string? PromptSnippet => null;
            public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

            public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ToolResult.Success("Hello from {{suffix}}!"));
            }
        }
        """;

    /// <summary>
    ///     Per-test fixture: creates a unique temp <c>~/.harbor</c>-like directory with a
    ///     <c>plugins/</c> subdirectory. Disposes on test completion.
    /// </summary>
    private sealed class PluginTestFixture : IDisposable
    {
        private readonly string _tempRoot;
        public PluginTestFixture(string tempRoot)
        {
            _tempRoot = tempRoot;
            HarborDir = Path.Combine(tempRoot, "harbor");
            PluginsDir = Path.Combine(HarborDir, "plugins");
            Directory.CreateDirectory(PluginsDir);
        }

        public string HarborDir { get; }
        public string PluginsDir { get; }

        public static async Task<PluginTestFixture> CreateAsync(string uniqueSuffix)
        {
            string root = Path.Combine(Path.GetTempPath(), "harbor-tests-" + uniqueSuffix + "-" + Guid.NewGuid().ToString("N"));
            var fixture = new PluginTestFixture(root);
            await Task.CompletedTask.ConfigureAwait(false);
            return fixture;
        }

        public async Task WritePluginAsync(string source)
        {
            string path = Path.Combine(PluginsDir, "plugin.cs");
            await File.WriteAllTextAsync(path, source).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    ///     In-memory <see cref="IPluginLoadHost" /> for tests. Captures all Register* calls
    ///     so tests can assert on them.
    /// </summary>
    private sealed class FakePluginLoadHost : IPluginLoadHost
    {
        private readonly ConcurrentDictionary<string, ITool> _tools = new();
        private readonly ConcurrentDictionary<ProviderId, Func<ILlmClient>> _providers = new();
        private readonly ConcurrentDictionary<AgentName, AgentDefinition> _agents = new();
        private readonly List<ITuiPlugin> _tuiPlugins = new();
        private readonly List<IPanelProvider> _panelProviders = new();
        private readonly IEventBus _eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;
        public IEventBus EventBus => _eventBus;

        public IReadOnlyList<ITool> RegisteredTools => _tools.Values.ToArray();
        public IReadOnlyList<ProviderId> RegisteredProviderIds => _providers.Keys.ToArray();
        public IReadOnlyList<AgentDefinition> RegisteredAgents => _agents.Values.ToArray();
        public IReadOnlyList<ITuiPlugin> RegisteredTuiPlugins
        {
            get { lock (_tuiPlugins) { return _tuiPlugins.ToArray(); } }
        }
        public IReadOnlyList<IPanelProvider> RegisteredPanelProviders
        {
            get { lock (_panelProviders) { return _panelProviders.ToArray(); } }
        }

        public Result RegisterTool(ITool tool)
        {
            return _tools.TryAdd(tool.Name.Value, tool)
                ? Result.Success()
                : Result.Failure($"Tool '{tool.Name}' already registered.");
        }

        public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory)
        {
            _providers[providerId] = factory;
            return Result.Success();
        }

        public Result RegisterAgent(AgentDefinition agent)
        {
            return _agents.TryAdd(agent.Name, agent)
                ? Result.Success()
                : Result.Failure($"Agent '{agent.Name}' already registered.");
        }

        public Result RegisterTuiPlugin(ITuiPlugin plugin)
        {
            lock (_tuiPlugins) { _tuiPlugins.Add(plugin); }
            return Result.Success();
        }

        public Result RegisterPanelProvider(IPanelProvider panel)
        {
            lock (_panelProviders) { _panelProviders.Add(panel); }
            return Result.Success();
        }
    }
}
