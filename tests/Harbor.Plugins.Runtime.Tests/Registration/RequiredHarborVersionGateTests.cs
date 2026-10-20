using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Registration;

/// <summary>
///     #1721: the registration boundary must refuse a plugin that needs a newer
///     Harbor BEFORE the plugin runs anything. Before the gate, such a plugin
///     loaded silently and failed later, somewhere inside the plugin, with a
///     symptom that said nothing about versions.
/// </summary>
public sealed class RequiredHarborVersionGateTests
{
    /// <summary>
    ///     A plugin whose declared floor is at or under the running Harbor loads.
    ///     The boundary is inclusive, and a floor under the runtime is satisfied
    ///     by it.
    /// </summary>
    [Test]
    [Arguments("0.4.0", "0.4.0", DisplayName = "FloorEqualToTheRuntime_Loads")]
    [Arguments("0.4.0", "0.2.0", DisplayName = "FloorUnderTheRuntime_Loads")]
    public async Task Registrar_FloorAtOrUnderTheRuntime_LoadsThePlugin(
        string runtimeVersion,
        string requiredVersion)
    {
        var host = new FakePluginLoadHost();
        var registrar = NewRegistrar(Version.Parse(runtimeVersion));
        var plugin = new VersionedToolPlugin("gate-floor", Version.Parse(requiredVersion));

        var result = registrar.Register(Loaded(plugin), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.WasInitialized).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A Harbor NEWER than the plugin asks for is a decision, not a guess: the
    ///     plugin loads, because the declaration is a minimum and Harbor keeps its
    ///     promises across minor and patch releases.
    /// </summary>
    [Test]
    public async Task Registrar_RuntimeNewerThanRequired_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = NewRegistrar(new Version(1, 0, 0));
        var plugin = new VersionedToolPlugin("gate-newer-runtime", new Version(0, 4, 0));

        var result = registrar.Register(Loaded(plugin), host);

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("a Harbor newer than the plugin requires still satisfies the plugin's floor");
        await Assert.That(plugin.WasInitialized).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A plugin that asks for a NEWER Harbor than the running one is refused,
    ///     and refused BEFORE it is initialized, so it leaves nothing behind: no
    ///     tool in the registry, no half-initialized plugin state.
    /// </summary>
    [Test]
    public async Task Registrar_RequiredVersionNewerThanRuntime_RefusesThePluginBeforeItRuns()
    {
        var host = new FakePluginLoadHost();
        var registrar = NewRegistrar(new Version(0, 4, 0));
        var plugin = new VersionedToolPlugin("gate-newer", new Version(0, 6, 0));

        var result = registrar.Register(Loaded(plugin), host);

        await Assert.That(result.IsFailure).IsTrue()
            .Because("a plugin that needs a Harbor this runtime is not must not load");
        await Assert.That(plugin.WasInitialized).IsFalse()
            .Because("the refusal happens in front of IPlugin.Initialize, so the incompatible plugin "
                   + "never runs");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("a refused plugin contributes nothing to the host");
    }

    /// <summary>
    ///     The refusal names the plugin, the Harbor version it needs and the Harbor
    ///     version that is running. That is the whole difference between "you have
    ///     a version problem" and a null-reference in the middle of the plugin.
    /// </summary>
    [Test]
    public async Task Registrar_Refusal_NamesThePluginTheFloorAndTheRuntime()
    {
        var host = new FakePluginLoadHost();
        var registrar = NewRegistrar(new Version(0, 4, 0));
        var plugin = new VersionedToolPlugin("gate-message", new Version(0, 6, 0));

        var result = registrar.Register(Loaded(plugin), host);

        await Assert.That(result.Error).Contains("gate-message");
        await Assert.That(result.Error).Contains("0.6.0");
        await Assert.That(result.Error).Contains("0.4.0");
    }

    /// <summary>
    ///     A plugin that declares no floor has nothing to enforce, so the gate does
    ///     not refuse it.
    /// </summary>
    [Test]
    public async Task Registrar_NoDeclaredFloor_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = NewRegistrar(new Version(0, 4, 0));
        var plugin = new VersionedToolPlugin("gate-nofloor", null);

        var result = registrar.Register(Loaded(plugin), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.WasInitialized).IsTrue();
    }

    /// <summary>
    ///     The registrar checks against the version it also reports to the plugin.
    ///     A plugin told "you are running 0.4.0" must not be refused against a
    ///     different number.
    /// </summary>
    [Test]
    public async Task Registrar_DefaultRuntimeVersion_IsTheOneReportedToThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = new PluginRegistrar(
            "/home/me/.harbor/plugins",
            NullLogger<PluginRegistrar>.Instance,
            NullLoggerFactory.Instance);
        var plugin = new ContextCapturingPlugin("gate-default", PluginLifecycle.CurrentHarborVersion);

        var result = registrar.Register(Loaded(plugin), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.CapturedContext).IsNotNull();
        await Assert.That(plugin.CapturedContext!.HarborVersion)
            .IsEqualTo(PluginLifecycle.CurrentHarborVersion);
    }

    private static PluginRegistrar NewRegistrar(Version harborVersion)
        => new(
            "/home/me/.harbor/plugins",
            NullLogger<PluginRegistrar>.Instance,
            NullLoggerFactory.Instance,
            audit: null,
            harborVersion: harborVersion);

    private static LoadedPlugin Loaded(IPlugin plugin)
        => new(
            plugin,
            plugin.Name,
            plugin.Version,
            plugin.GetType(),
            "/home/me/.harbor/plugins/" + plugin.Name + ".cs",
            "abc",
            false);

    private sealed class VersionedToolPlugin : IToolPlugin
    {
        private readonly Version? _requiredHarborVersion;

        public VersionedToolPlugin(string name, Version? requiredHarborVersion)
        {
            Name = name;
            _requiredHarborVersion = requiredHarborVersion;
        }

        public bool WasInitialized { get; private set; }

        public string Name { get; }

        public Version Version => new(1, 0, 0);

        public Version RequiredHarborVersion => _requiredHarborVersion!;

        public string Description => "plugin with a configurable Harbor floor";

        public void Initialize(PluginContext context) => WasInitialized = true;

        public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool(new GateTool());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ContextCapturingPlugin : IPlugin
    {
        private readonly Version _requiredHarborVersion;

        public ContextCapturingPlugin(string name, Version requiredHarborVersion)
        {
            Name = name;
            _requiredHarborVersion = requiredHarborVersion;
        }

        public PluginContext? CapturedContext { get; private set; }

        public string Name { get; }

        public Version Version => new(1, 0, 0);

        public Version RequiredHarborVersion => _requiredHarborVersion;

        public string Description => "captures the context it was initialized with";

        public void Initialize(PluginContext context) => CapturedContext = context;

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class GateTool : ITool
    {
        public GateTool() { Name = ToolName.Create("gate_tool"); }

        public ToolName Name { get; }

        /// <inheritdoc />
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "gate tool";

        public string Description => "gate tool";

        public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("ok"));
    }
}
