using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Plugins.Abstractions.Tests;

public class PluginCapabilitiesTests
{
    [Test]
    public async Task TryParse_EmptyString_ReturnsEmptySet()
    {
        var result = PluginCapabilities.TryParse(null);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryParse_SingleCapability_ReturnsSingleEntry()
    {
        var result = PluginCapabilities.TryParse("read_files");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).Contains(PluginCapability.ReadFiles);
    }

    [Test]
    public async Task TryParse_MultipleCapabilities_ReturnsAllEntries()
    {
        var result = PluginCapabilities.TryParse("read_files,write_files,run_processes");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(3);
        await Assert.That(result.Value).Contains(PluginCapability.ReadFiles);
        await Assert.That(result.Value).Contains(PluginCapability.WriteFiles);
        await Assert.That(result.Value).Contains(PluginCapability.RunProcesses);
    }

    [Test]
    public async Task TryParse_UnknownCapability_ReturnsFailure()
    {
        var result = PluginCapabilities.TryParse("unknown_capability");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task TryParse_CaseSensitive_UnknownFails()
    {
        var result = PluginCapabilities.TryParse("Read_Files");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task All_ContainsAllSixCapabilities()
    {
        await Assert.That(PluginCapabilities.All.Count).IsEqualTo(6);
        await Assert.That(PluginCapabilities.All).Contains(PluginCapability.HttpRequests);
        await Assert.That(PluginCapabilities.All).Contains(PluginCapability.SubAgents);
        await Assert.That(PluginCapabilities.All).Contains(PluginCapability.ReadEnv);
    }

    [Test]
    public async Task ToName_ReadFiles_ReturnsCanonicalName()
    {
        await Assert.That(PluginCapabilities.ToName(PluginCapability.ReadFiles)).IsEqualTo("read_files");
    }

    [Test]
    public async Task ToManifestString_RoundTripsThroughParse()
    {
        var capabilities = new HashSet<PluginCapability>
        {
            PluginCapability.ReadFiles,
            PluginCapability.HttpRequests
        };
        var manifest = PluginCapabilities.ToManifestString(capabilities);
        var parsed = PluginCapabilities.TryParse(manifest);
        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value).Contains(PluginCapability.ReadFiles);
        await Assert.That(parsed.Value).Contains(PluginCapability.HttpRequests);
    }
}

public class LoadedPluginTests
{
    [Test]
    public async Task LoadedPlugin_DisplayName_ContainsNameAndVersion()
    {
        var plugin = new StubPlugin("test-plugin", "1.0.0");
        var loaded = new LoadedPlugin(
            plugin,
            "test-plugin",
            new Version(1, 0, 0),
            typeof(StubPlugin),
            "/path/to/plugin.cs",
            "abc123hash",
            false);

        await Assert.That(loaded.DisplayName).Contains("test-plugin");
        await Assert.That(loaded.DisplayName).Contains("1.0.0");
    }

    [Test]
    public async Task LoadedPlugin_ConvenienceCtor_SetsEmptyCapabilities()
    {
        var plugin = new StubPlugin("test-plugin", "1.0.0");
        var loaded = new LoadedPlugin(
            plugin,
            "test-plugin",
            new Version(1, 0, 0),
            typeof(StubPlugin),
            "/path/to/plugin.cs",
            "abc123hash",
            true);

        await Assert.That(loaded.LoadedFromCache).IsTrue();
        await Assert.That(loaded.DeclaredCapabilities.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoadedPlugin_FullCtor_SetsDeclaredCapabilities()
    {
        var plugin = new StubPlugin("test-plugin", "1.0.0");
        var capabilities = new HashSet<PluginCapability>
        {
            PluginCapability.ReadFiles,
            PluginCapability.WriteFiles
        };
        var loaded = new LoadedPlugin(
            plugin,
            "test-plugin",
            new Version(1, 0, 0),
            typeof(StubPlugin),
            "/path/to/plugin.cs",
            "abc123hash",
            false,
            capabilities);

        await Assert.That(loaded.DeclaredCapabilities).Contains(PluginCapability.ReadFiles);
        await Assert.That(loaded.DeclaredCapabilities).Contains(PluginCapability.WriteFiles);
    }

    [Test]
    public async Task LoadedPlugin_Properties_AreSetCorrectly()
    {
        var plugin = new StubPlugin("my-plugin", "2.0.0");
        var loaded = new LoadedPlugin(
            plugin,
            "my-plugin",
            new Version(2, 0, 0),
            typeof(StubPlugin),
            "/path/to/plugin.cs",
            "hash456",
            true);

        await Assert.That(loaded.Name).IsEqualTo("my-plugin");
        await Assert.That(loaded.Version).IsEqualTo(new Version(2, 0, 0));
        await Assert.That(loaded.SourcePath).IsEqualTo("/path/to/plugin.cs");
        await Assert.That(loaded.SourceHash).IsEqualTo("hash456");
        await Assert.That(loaded.Instance).IsSameReferenceAs(plugin);
    }
}

public class IPluginContractTests
{
    [Test]
    public async Task IPlugin_ImplementingClass_ExposesName()
    {
        var plugin = new StubPlugin("test", "1.0.0");
        await Assert.That(plugin.Name).IsEqualTo("test");
    }

    [Test]
    public async Task IPlugin_ImplementingClass_ExposesVersion()
    {
        var plugin = new StubPlugin("test", "1.0.0");
        await Assert.That(plugin.Version).IsEqualTo(new Version(1, 0, 0));
    }

    [Test]
    public async Task IPlugin_ImplementingClass_ExposesDescription()
    {
        var plugin = new StubPlugin("test", "1.0.0");
        await Assert.That(plugin.Description).IsNotNull();
    }

    [Test]
    public async Task IPlugin_Initialize_CanBeCalled()
    {
        var plugin = new StubPlugin("test", "1.0.0");
        plugin.Initialize(new PluginContext
        {
            Services = new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            Configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            LoggerFactory = NullLoggerFactory.Instance,
            EventBus = new Harbor.TestKit.FakeEventBus(),
            PluginDirectory = "/tmp/plugins",
            DataDirectory = "/tmp/plugins/data"
        });
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task IPlugin_ShutdownAsync_CompletesWithoutError()
    {
        var plugin = new StubPlugin("test", "1.0.0");
        await plugin.ShutdownAsync(CancellationToken.None);
        await Assert.That(true).IsTrue();
    }
}

public sealed class StubPlugin : IPlugin
{
    private readonly string _name;
    private readonly Version _version;

    public StubPlugin(string name, string version)
    {
        _name = name;
        _version = Version.Parse(version);
    }

    public string Name => _name;
    public Version Version => _version;
    public Version RequiredHarborVersion => new(0, 1, 0);
    public string Description => $"Stub plugin: {_name}";
    public void Initialize(PluginContext context) { }
    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
