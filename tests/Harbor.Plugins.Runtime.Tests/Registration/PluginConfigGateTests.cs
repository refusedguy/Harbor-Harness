using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Registration;

/// <summary>
///     #1734: a plugin's own settings are checked against the schema the
///     plugin declared, BEFORE the plugin starts. A plugin that would start
///     with settings it could not read is a plugin that fails later, in a
///     place that has nothing to do with the mistake.
/// </summary>
public sealed class PluginConfigGateTests
{
    /// <summary>
    ///     A section that satisfies the schema starts the plugin, and the
    ///     plugin receives the values it declared.
    /// </summary>
    [Test]
    public async Task Registrar_SectionSatisfiesTheSchema_HandsThePluginItsValues()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(new StubConfigSource("""{ "maxResults": 10 }"""));

        Result result = registrar.Register(Loaded(plugin, Manifest()), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.Config).IsNotNull();
        await Assert.That(plugin.Config!["maxResults"].AsInteger()).IsEqualTo(10L);
    }

    /// <summary>
    ///     A setting the user did not write takes the default the schema
    ///     published, so the plugin reads a value either way.
    /// </summary>
    [Test]
    public async Task Registrar_SettingNotWritten_TakesTheDeclaredDefault()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(new StubConfigSource("{}"));

        Result result = registrar.Register(Loaded(plugin, Manifest()), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.Config!["maxResults"].AsInteger()).IsEqualTo(5L);
    }

    /// <summary>
    ///     A setting the schema does not declare refuses the plugin, and the
    ///     refusal names the setting: a user who cannot see which setting is
    ///     wrong cannot fix it.
    /// </summary>
    [Test]
    public async Task Registrar_SectionHasASettingTheSchemaDoesNotDeclare_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(new StubConfigSource("""{ "prot": 8080 }"""));

        Result result = registrar.Register(Loaded(plugin, Manifest()), host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("plugins.web-search.prot");
        await Assert.That(plugin.WasInitialized).IsFalse()
            .Because("the refusal happens in front of IPlugin.Initialize");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A setting outside the declared range refuses the plugin, and the
    ///     refusal names the range.
    /// </summary>
    [Test]
    public async Task Registrar_SectionIsOutOfRange_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(new StubConfigSource("""{ "maxResults": 100 }"""));

        Result result = registrar.Register(Loaded(plugin, Manifest()), host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("plugins.web-search.maxResults");
        await Assert.That(result.Error).Contains("50");
        await Assert.That(plugin.WasInitialized).IsFalse();
    }

    /// <summary>
    ///     A plugin that declares no schema and a configuration that has a
    ///     section for it: the host cannot know what the user meant, so it
    ///     says so instead of dropping the settings.
    /// </summary>
    [Test]
    public async Task Registrar_PluginWithoutASchemaAndASectionForIt_RefusesThePlugin()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(new StubConfigSource("""{ "maxResults": 10 }"""));

        Result result = registrar.Register(Loaded(plugin, new PluginManifest { Name = plugin.Name }), host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("plugins.web-search");
        await Assert.That(result.Error).Contains("maxResults");
        await Assert.That(plugin.WasInitialized).IsFalse();
    }

    /// <summary>
    ///     A plugin that declares no schema and has no section starts exactly
    ///     as it did before this feature existed.
    /// </summary>
    [Test]
    public async Task Registrar_PluginWithoutASchemaAndWithoutASection_LoadsThePlugin()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(new StubConfigSource("{}"));

        Result result = registrar.Register(Loaded(plugin, new PluginManifest { Name = plugin.Name }), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.WasInitialized).IsTrue();
        await Assert.That(plugin.Config).IsNotNull();
        await Assert.That(plugin.Config!.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A host that publishes no sections at all starts every plugin with
    ///     the settings its own schema declares.
    /// </summary>
    [Test]
    public async Task Registrar_HostWithoutAConfigSource_StartsThePluginWithTheDeclaredDefaults()
    {
        var host = new FakePluginLoadHost();
        var plugin = new ConfigPlugin("web-search");
        var registrar = NewRegistrar(config: null);

        Result result = registrar.Register(Loaded(plugin, Manifest()), host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.Config!["maxResults"].AsInteger()).IsEqualTo(5L);
    }

    private static PluginRegistrar NewRegistrar(IPluginConfigSource? config)
    {
        ILogger<PluginRegistrar> logger = NullLogger<PluginRegistrar>.Instance;
        return new PluginRegistrar(
            pluginRoot: "/home/me/.harbor/plugins",
            logger: logger,
            loggerFactory: NullLoggerFactory.Instance,
            config: config);
    }

    private static PluginManifest Manifest() => new()
    {
        Name = "web-search",
        Config = new PluginConfigSchema
        {
            Fields =
            [
                new PluginConfigField
                {
                    Name = "maxResults",
                    Type = PluginConfigFieldType.Integer,
                    Default = new PluginConfigValue { Type = PluginConfigFieldType.Integer, Text = "5" },
                    Minimum = 1,
                    Maximum = 50,
                },
            ],
        },
    };

    private static LoadedPlugin Loaded(IPlugin plugin, PluginManifest? manifest)
        => new(
            plugin,
            plugin.Name,
            plugin.Version,
            plugin.GetType(),
            "/home/me/.harbor/plugins/" + plugin.Name + ".cs",
            "abc",
            false,
            new HashSet<PluginCapability>(),
            manifest);

    /// <summary>A configuration source that answers with one fixed section.</summary>
    private sealed class StubConfigSource : IPluginConfigSource
    {
        private readonly JsonElement _section;

        internal StubConfigSource(string json) => _section = JsonDocument.Parse(json).RootElement;

        public JsonElement? Section(string pluginName) => _section;
    }

    /// <summary>A plugin that keeps the configuration it was handed.</summary>
    private sealed class ConfigPlugin : IPlugin
    {
        internal ConfigPlugin(string name) => Name = name;

        public string Name { get; }

        public Version Version => new(1, 0, 0);

        public Version RequiredHarborVersion => new(0, 4, 0);

        public string Description => "plugin with settings of its own";

        public bool WasInitialized { get; private set; }

        public IReadOnlyDictionary<string, PluginConfigValue>? Config { get; private set; }

        public void Initialize(PluginContext context)
        {
            WasInitialized = true;
            Config = context.Config;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
