using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Runtime.Tests.TestSupport;

namespace Harbor.Plugins.Runtime.Tests.Registration;

/// <summary>
///     #1739: the manifest is a DECLARATION, and the code is the truth. A
///     manifest that says something the plugin does not do is refused, with the
///     reason — because a plugin that ships two versions of itself is a plugin
///     the host cannot describe.
/// </summary>
public sealed class PluginManifestGateTests
{
    /// <summary>
    ///     A manifest that names the plugin the way the plugin names itself is
    ///     accepted, and the plugin registers its tool.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestAgreesWithThePlugin_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-ok");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest
            {
                Name = plugin.Name,
                Version = plugin.Version,
                Axes = ["IToolPlugin"],
            }),
            host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.WasInitialized).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A manifest that names the plugin differently is refused, and the
    ///     refusal names both names. An index built from the manifest would
    ///     otherwise list a plugin that does not exist.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestNamesThePluginDifferently_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-name");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { Name = "some-other-plugin" }),
            host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("some-other-plugin");
        await Assert.That(result.Error).Contains(plugin.Name);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("a refused plugin contributes nothing to the host");
    }

    /// <summary>
    ///     A manifest that declares a different version is refused, and the
    ///     refusal names both versions.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestDeclaresAnotherVersion_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-version");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { Version = new Version(9, 9, 9) }),
            host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("9.9.9");
        await Assert.That(result.Error).Contains("1.0.0");
    }

    /// <summary>
    ///     A manifest that declares the axes the plugin implements is accepted:
    ///     the declaration and the code agree.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestAxesMatchThePlugin_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-axes-ok");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { Axes = ["IToolPlugin"] }),
            host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A manifest that declares an axis the plugin does not implement is
    ///     refused, and the refusal names both sides. A plugin that claims to
    ///     contribute a provider and contributes nothing is a plugin the host
    ///     cannot describe.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestDeclaresAnAxisThePluginDoesNotImplement_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-axes-over");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { Axes = ["IToolPlugin", "IProviderPlugin"] }),
            host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("IProviderPlugin");
        await Assert.That(result.Error).Contains("IToolPlugin");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A manifest that declares FEWER axes than the plugin implements is
    ///     refused too: the declaration is exact, not a lower bound.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestDeclaresFewerAxesThanThePluginImplements_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-axes-under");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { Axes = [] }),
            host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("IToolPlugin");
    }

    /// <summary>
    ///     A manifest that says nothing about axes makes no claim, so there is
    ///     nothing to disagree with.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestWithoutAxes_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-no-axes");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { Name = plugin.Name }),
            host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A manifest that asks for a Harbor newer than the running one is
    ///     refused by the same gate that refuses the in-code floor.
    /// </summary>
    [Test]
    public async Task Registrar_ManifestAsksForANewerHarbor_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar(harborVersion: new Version(0, 4, 0));
        var plugin = new ManifestToolPlugin("manifest-harbor");

        var result = registrar.Register(
            PluginGateFixture.Loaded(plugin, new PluginManifest { RequiredHarborVersion = new Version(0, 6, 0) }),
            host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("0.6.0");
        await Assert.That(result.Error).Contains("0.4.0");
        await Assert.That(plugin.WasInitialized).IsFalse()
            .Because("the refusal happens in front of IPlugin.Initialize");
    }

    /// <summary>
    ///     A plugin with no manifest registers exactly as it did before the
    ///     manifest existed.
    /// </summary>
    [Test]
    public async Task Registrar_PluginWithoutAManifest_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var registrar = PluginGateFixture.NewRegistrar();
        var plugin = new ManifestToolPlugin("manifest-none");

        var result = registrar.Register(PluginGateFixture.Loaded(plugin, null), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    private sealed class ManifestToolPlugin : IToolPlugin
    {
        public ManifestToolPlugin(string name) => Name = name;

        public bool WasInitialized { get; private set; }

        public string Name { get; }

        public Version Version => new(1, 0, 0);

        public Version RequiredHarborVersion => new(0, 4, 0);

        public string Description => "plugin with a manifest beside it";

        public void Initialize(PluginContext context) => WasInitialized = true;

        public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool(new ManifestGateTool());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ManifestGateTool : ITool
    {
        public ManifestGateTool() => Name = ToolName.Create("manifest_gate_tool");

        public ToolName Name { get; }

        /// <inheritdoc />
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "manifest gate tool";

        public string Description => "manifest gate tool";

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
