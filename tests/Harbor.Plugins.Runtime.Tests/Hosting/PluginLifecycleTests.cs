using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Tests for the plugin LIFECYCLE MODEL (#1737): define, run, stop, update,
///     undefine — and the state each of them leaves the host in.
/// </summary>
/// <remarks>
///     <para>
///         The host's own load path is covered by <see cref="HostingLayerTests" /> and
///         the composition document by <see cref="PluginInventoryTests" />. These tests
///         drive the lifecycle operations directly, so each one can assert on what the
///         host's registries actually hold afterwards.
///     </para>
/// </remarks>
public sealed class PluginLifecycleTests
{
    /// <summary>
    ///     A plugin that runs is ACTIVE, and the composition says what it registered.
    /// </summary>
    [Test]
    public async Task Load_RunningPlugin_ReportsActiveWithItsRegistrations()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-running").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v1"),
            fixture.PluginsDir);

        var load = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(load.IsSuccess).IsTrue();

        PluginInventoryEntry? entry = EntryOf(pluginHost, "lifecycle-plugin");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Active);
        await Assert.That(entry.Registrations.Count).IsEqualTo(1);
        await Assert.That(entry.Registrations[0].Subject).IsEqualTo("lifecycle_tool_v1");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     Stop takes back EXACTLY what the plugin registered, and KEEPS the
    ///     definition: the plugin can be run again.
    /// </summary>
    [Test]
    public async Task Stop_TakesBackTheRegistrations_AndKeepsTheDefinition()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-stop").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var plugin = new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v1");
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => plugin,
            fixture.PluginsDir);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);

        var stopped = await pluginHost.StopAsync("lifecycle-plugin").ConfigureAwait(false);

        await Assert.That(stopped.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("a stop takes back exactly what the run registered — the tool is gone");
        await Assert.That(plugin.ShutdownCount).IsEqualTo(1)
            .Because("a stop shuts the plugin down; ShutdownAsync is declared for exactly this");

        PluginInventoryEntry? entry = EntryOf(pluginHost, "lifecycle-plugin");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Stopped);

        await Assert.That(pluginHost.Definitions.Count).IsEqualTo(1)
            .Because("stopping keeps the definition: the plugin is known, it is just not running");
    }

    /// <summary>
    ///     An update REPLACES the running version: the old registration is taken
    ///     back and the new one lands, under the same name.
    /// </summary>
    [Test]
    public async Task Update_ReplacesTheRunningVersion()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-update").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var first = new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v1");
        var second = new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v2");
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => first,
            fixture.PluginsDir);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);

        var updated = new LoadedPlugin(
            second,
            second.Name,
            second.Version,
            typeof(LifecyclePlugin),
            "lifecycle.cs",
            "source-v2-hash",
            false);

        var result = await pluginHost.UpdateAsync(updated, host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1)
            .Because("the old registration was taken back and the new one took its place — one tool, not two");
        await Assert.That(host.RegisteredTools[0].Name.Value).IsEqualTo("lifecycle_tool_v2");
        await Assert.That(first.ShutdownCount).IsEqualTo(1)
            .Because("the replaced version is shut down, not left running");

        PluginInventoryEntry? entry = EntryOf(pluginHost, "lifecycle-plugin");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Active);
    }

    /// <summary>
    ///     A FAILED run leaves nothing behind: the registration that landed before
    ///     the refusal is taken back.
    /// </summary>
    [Test]
    public async Task RefusedRegistration_LeavesNothingBehind()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-refused").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();

        // A tool the host already holds: the plugin's second registration collides
        // with it and is refused.
        host.RegisterTool(new LifecycleTool("collide"));

        var plugin = new LifecyclePlugin(
            "lifecycle-plugin",
            "lifecycle_tool_v1",
            "collide");
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => plugin,
            fixture.PluginsDir);

        var load = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(load.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1)
            .Because("the tool that landed before the refusal was taken back — a failed run never lingers");

        PluginInventoryEntry? entry = EntryOf(pluginHost, "lifecycle-plugin");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Failed);
        await Assert.That(entry.FailureReason).IsNotNull();
    }

    /// <summary>
    ///     An incompatible plugin is refused AT START, and the refusal is visible.
    /// </summary>
    [Test]
    public async Task IncompatiblePlugin_IsRefusedAndTheStateSaysWhy()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-incompatible").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => new LifecyclePlugin("lifecycle-plugin", new Version(99, 0, 0), "lifecycle_tool_v1"),
            fixture.PluginsDir);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("a plugin that needs a Harbor this build is not, contributes nothing");

        PluginInventoryEntry? entry = EntryOf(pluginHost, "lifecycle-plugin");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Failed);
        await Assert.That(entry.FailureReason).IsNotNull()
            .Because("a refusal nobody can read is a refusal nobody can act on");
    }

    /// <summary>
    ///     Running a plugin that is already running is refused, and the refusal
    ///     says what to do about it.
    /// </summary>
    [Test]
    public async Task Run_WhileRunning_RefusesAndSaysStopFirst()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-run-twice").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v1"),
            fixture.PluginsDir);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        var again = await pluginHost.RunAsync("lifecycle-plugin", host).ConfigureAwait(false);

        await Assert.That(again.IsFailure).IsTrue();
        await Assert.That(again.Error).Contains("Stop it before running it again");
    }

    /// <summary>
    ///     Undefine forgets the definition — and refuses while the plugin runs.
    /// </summary>
    [Test]
    public async Task Undefine_ForgetsTheDefinition_AndRefusesWhileRunning()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-undefine").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v1"),
            fixture.PluginsDir);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        var whileRunning = await pluginHost.UndefineAsync("lifecycle-plugin").ConfigureAwait(false);
        await Assert.That(whileRunning.IsFailure).IsTrue()
            .Because("forgetting a definition while its registrations are live would leave a plugin "
                     + "nobody can name and nobody can stop");

        await pluginHost.StopAsync("lifecycle-plugin").ConfigureAwait(false);

        var forgotten = await pluginHost.UndefineAsync("lifecycle-plugin").ConfigureAwait(false);
        await Assert.That(forgotten.IsSuccess).IsTrue();
        await Assert.That(pluginHost.Definitions.Count).IsEqualTo(0);
        await Assert.That(EntryOf(pluginHost, "lifecycle-plugin")).IsNull()
            .Because("a plugin the host has forgotten is a plugin the host does not know about");
    }

    /// <summary>
    ///     The load is IDEMPOTENT: loading the same source twice does not register
    ///     the same plugin twice.
    /// </summary>
    [Test]
    public async Task Load_IsIdempotent_SecondLoadDoesNotRegisterAgain()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-idempotent").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        PluginHost pluginHost = BuildHost(
            new InMemoryPluginSource(
                new[] { new PluginScript("lifecycle.cs", "source-v1") }),
            _ => new LifecyclePlugin("lifecycle-plugin", "lifecycle_tool_v1"),
            fixture.PluginsDir);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);
        var second = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
        await Assert.That(pluginHost.ActivePlugins.Count).IsEqualTo(1);

        PluginInventoryEntry? entry = EntryOf(pluginHost, "lifecycle-plugin");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Active)
            .Because("the run that is already live IS this plugin — a second registration would "
                     + "collide with the first");
    }

    /// <summary>
    ///     The fate of a source file that never became a plugin is visible too: the
    ///     composition says which file failed and why.
    /// </summary>
    [Test]
    public async Task Load_BrokenSource_ReportsTheFailureUnderTheFileName()
    {
        using var fixture = await PluginTestFixture.CreateAsync("lifecycle-broken").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var references = new PluginAssemblyReferences(NullLogger<PluginAssemblyReferences>.Instance);

        PluginHost pluginHost = new PluginHostBuilder()
            .WithSource(new InMemoryPluginSource(
                new[] { new PluginScript("broken.cs", SamplePluginSource.Broken()) }))
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                fixture.CacheDir,
                NullLogger<CachingCompiler>.Instance))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(fixture.PluginsDir, NullLogger<PluginRegistrar>.Instance, NullLoggerFactory.Instance),
                NullLogger.Instance))
            .WithOptions(o =>
            {
                o.PluginRoot = fixture.PluginsDir;
                o.ContinueOnError = true;
            })
            .Build(NullLogger<PluginHost>.Instance);

        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        PluginInventoryEntry? entry = null;
        foreach (PluginInventoryEntry candidate in pluginHost.Inventory.Entries)
        {
            if (candidate.SourcePath.EndsWith("broken.cs", StringComparison.Ordinal))
            {
                entry = candidate;
            }
        }

        await Assert.That(entry).IsNotNull()
            .Because("a plugin that did not compile still has a fate, and the fate is visible");
        await Assert.That(entry!.Phase).IsEqualTo(PluginInventoryPhase.Failed);
        await Assert.That(entry.FailureReason).IsNotNull();
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The composition's entry for one plugin, or <c>null</c> when the host does
    ///     not know it.
    /// </summary>
    private static PluginInventoryEntry? EntryOf(PluginHost host, string name)
    {
        foreach (PluginInventoryEntry entry in host.Inventory.Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    ///     A host over a fake compiler and instantiator: the lifecycle is what is
    ///     under test here, not the Roslyn pipeline (which
    ///     <see cref="HostingLayerTests" /> covers).
    /// </summary>
    private static PluginHost BuildHost(
        IPluginSource source,
        Func<string, IPlugin> pluginBySource,
        string pluginRoot)
    {
        return new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(new ScriptCompiler())
            .WithInstantiator(new ScriptInstantiator(pluginBySource))
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(pluginRoot, NullLogger<PluginRegistrar>.Instance, NullLoggerFactory.Instance),
                NullLogger.Instance))
            .WithOptions(o =>
            {
                o.PluginRoot = pluginRoot;
                o.ContinueOnError = true;
            })
            .Build(NullLogger<PluginHost>.Instance);
    }

    /// <summary>
    ///     A compiler that hands back the test assembly and the script's own
    ///     content hash — enough for the host to tell one version of a plugin
    ///     from another.
    /// </summary>
    private sealed class ScriptCompiler : IPluginCompiler
    {
        public Task<Result<CompiledPluginAssembly>> CompileAsync(
            PluginScript script,
            CancellationToken ct = default)
        {
            return Task.FromResult(Result.Success(new CompiledPluginAssembly(
                typeof(ScriptCompiler).Assembly,
                script.Hash,
                script.Path)));
        }
    }

    /// <summary>
    ///     An instantiator that builds the plugin the test asked for.
    /// </summary>
    private sealed class ScriptInstantiator : IPluginInstantiator
    {
        private readonly Func<string, IPlugin> _pluginBySource;

        internal ScriptInstantiator(Func<string, IPlugin> pluginBySource)
        {
            _pluginBySource = pluginBySource;
        }

        public Result<IReadOnlyList<LoadedPlugin>> Instantiate(CompiledPluginAssembly compiled)
        {
            IPlugin plugin = _pluginBySource(compiled.SourceHash);
            return Result.Success<IReadOnlyList<LoadedPlugin>>(
            [
                new LoadedPlugin(
                    plugin,
                    plugin.Name,
                    plugin.Version,
                    plugin.GetType(),
                    compiled.SourcePath,
                    compiled.SourceHash,
                    false),
            ]);
        }
    }

    /// <summary>
    ///     A plugin that registers one or more tools and counts its shutdowns.
    /// </summary>
    private sealed class LifecyclePlugin : IToolPlugin
    {
        private readonly string[] _toolNames;

        internal LifecyclePlugin(
            string name,
            params string[] toolNames)
            : this(name, new Version(0, 4, 0), toolNames)
        {
        }

        internal LifecyclePlugin(
            string name,
            Version requiredHarborVersion,
            params string[] toolNames)
        {
            Name = name;
            RequiredHarborVersion = requiredHarborVersion;
            _toolNames = toolNames;
        }

        public string Name { get; }

        public Version Version => new(1, 0, 0);

        public Version RequiredHarborVersion { get; }

        public string Description => "lifecycle test plugin";

        public int ShutdownCount { get; private set; }

        public void Initialize(PluginContext context)
        {
        }

        public void RegisterTools(IToolRegistryBuilder builder)
        {
            foreach (string toolName in _toolNames)
            {
                builder.AddTool(new LifecycleTool(toolName));
            }
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     A tool the lifecycle tests register and unregister.
    /// </summary>
    private sealed class LifecycleTool : ITool
    {
        private readonly ToolName _name;

        internal LifecycleTool(string name)
        {
            _name = ToolName.Create(name);
        }

        public ToolName Name => _name;

        public string DisplayName => _name.Value;

        public string Description => "lifecycle test tool";

        public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolResult.Success("lifecycle"));
        }
    }
}
