// ConfigStoreAotTests.cs — regression tests for the NativeAOT-safe config
// persistence path (task A6).
//
// Guards the invariant that JsonCommonConfigStore round-trips through the
// SOURCE-GENERATED ConfigJsonContext instead of reflection-based
// System.Text.Json overloads (which crash or silently degrade to defaults
// under NativeAOT), and that malformed input surfaces as Result.Failure
// rather than an exception.

using System.Collections.Immutable;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Round-trip and failure-mode tests for <see cref="JsonCommonConfigStore" />
///     against a temp directory. The store's public API is exercised exactly as
///     composition roots use it — no reflection, no mocks.
/// </summary>
public class ConfigStoreAotTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "harbor-config-tests-" + Guid.NewGuid().ToString("N"));

    private static CommonConfig NewPopulatedConfig(string dir) => new()
    {
        ConfigDirectory = dir,
        OnboardingCompleted = true,
        Theme = "dark",
        ApiKeys = ImmutableDictionary<string, string>.Empty.Add("anthropic", "sk-test-1234"),
        DefaultProvider = "kilocode",
        DefaultModel = "tencent/hy3:free",
        DefaultAgent = "plan",
        StorageBackend = "sqlite",
        StoragePath = "/tmp/harbor-sessions",
        LogLevel = "debug",
        EnableFileLogging = false,
        MaxLogFiles = 7,
        PermissionMode = "strict",
        AlwaysAllowTools = ImmutableList.Create("read", "glob"),
        AlwaysDenyTools = ImmutableList.Create("bash"),
        EnablePlugins = false,
        PluginDirectories = "/opt/plugins",
        EnableScripting = false,
        HttpProxy = "http://proxy.corp:3128",
        HttpTimeoutSeconds = 45,
        UserAgent = "HarborTest/1.0",
        CompactionReserveTokens = 4096,
        CompactionKeepRecentTokens = 8192,
        CompactionTailTurns = 5,
    };

    [Test]
    public async Task SaveThenLoad_RoundTripsPopulatedConfig_ThroughSourceGeneratedContext()
    {
        string dir = NewTempDir();
        try
        {
            var original = NewPopulatedConfig(dir);
            var store = new JsonCommonConfigStore(original, NullLogger<JsonCommonConfigStore>.Instance);

            var saveResult = await store.SaveAsync(original);
            await Assert.That(saveResult.IsSuccess).IsTrue();

            var loadResult = await store.LoadAsync();
            await Assert.That(loadResult.IsSuccess).IsTrue();
            var loaded = loadResult.Value;

            // Strings + defaults.
            await Assert.That(loaded.ConfigVersion).IsEqualTo("1");
            await Assert.That(loaded.Theme).IsEqualTo("dark");
            await Assert.That(loaded.DefaultProvider).IsEqualTo("kilocode");
            await Assert.That(loaded.DefaultModel).IsEqualTo("tencent/hy3:free");
            await Assert.That(loaded.DefaultAgent).IsEqualTo("plan");
            await Assert.That(loaded.StorageBackend).IsEqualTo("sqlite");
            await Assert.That(loaded.StoragePath).IsEqualTo("/tmp/harbor-sessions");
            await Assert.That(loaded.LogLevel).IsEqualTo("debug");
            await Assert.That(loaded.PermissionMode).IsEqualTo("strict");
            await Assert.That(loaded.PluginDirectories).IsEqualTo("/opt/plugins");
            await Assert.That(loaded.HttpProxy).IsEqualTo("http://proxy.corp:3128");
            await Assert.That(loaded.UserAgent).IsEqualTo("HarborTest/1.0");

            // Bools.
            await Assert.That(loaded.OnboardingCompleted).IsTrue();
            await Assert.That(loaded.EnableFileLogging).IsFalse();
            await Assert.That(loaded.EnablePlugins).IsFalse();
            await Assert.That(loaded.EnableScripting).IsFalse();

            // Ints.
            await Assert.That(loaded.MaxLogFiles).IsEqualTo(7);
            await Assert.That(loaded.HttpTimeoutSeconds).IsEqualTo(45);
            await Assert.That(loaded.CompactionReserveTokens).IsEqualTo(4096);
            await Assert.That(loaded.CompactionKeepRecentTokens).IsEqualTo(8192);
            await Assert.That(loaded.CompactionTailTurns).IsEqualTo(5);

            // Immutable collections (converter-covered property types).
            await Assert.That(loaded.ApiKeys["anthropic"]).IsEqualTo("sk-test-1234");
            await Assert.That(loaded.AlwaysAllowTools.Count).IsEqualTo(2);
            await Assert.That(loaded.AlwaysAllowTools[0]).IsEqualTo("read");
            await Assert.That(loaded.AlwaysAllowTools[1]).IsEqualTo("glob");
            await Assert.That(loaded.AlwaysDenyTools.Count).IsEqualTo(1);
            await Assert.That(loaded.AlwaysDenyTools[0]).IsEqualTo("bash");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_MalformedJson_ReturnsFailureWithoutThrowing()
    {
        string dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var defaults = new CommonConfig { ConfigDirectory = dir };
            var store = new JsonCommonConfigStore(defaults, NullLogger<JsonCommonConfigStore>.Instance);
            await File.WriteAllTextAsync(Path.Combine(dir, "config.json"), "{ this is } not json");

            var result = await store.LoadAsync();

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error.Contains("corrupt")).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_MissingFile_ReturnsSuppliedDefaultInstance()
    {
        string dir = NewTempDir();
        try
        {
            var defaults = new CommonConfig { ConfigDirectory = dir, Theme = "light" };
            var store = new JsonCommonConfigStore(defaults, NullLogger<JsonCommonConfigStore>.Instance);

            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Theme).IsEqualTo("light");
            await Assert.That(File.Exists(defaults.ConfigFilePath)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Save_PreservesForeignFields_InExistingFile()
    {
        string dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");

            // Simulate a legacy HarborConfig file with fields CommonConfig
            // does not own; the merge-on-save path must keep them.
            await File.WriteAllTextAsync(
                path,
                """{ "provider": "openai", "customFutureField": 123 }""");

            var original = NewPopulatedConfig(dir);
            var store = new JsonCommonConfigStore(original, NullLogger<JsonCommonConfigStore>.Instance);

            var saveResult = await store.SaveAsync(original);
            await Assert.That(saveResult.IsSuccess).IsTrue();

            string merged = await File.ReadAllTextAsync(path);
            await Assert.That(merged.Contains("customFutureField")).IsTrue();

            // The common fields must be written camelCase (Web semantics kept
            // by the source-generated context) so non-.NET readers stay happy.
            await Assert.That(merged.Contains("\"theme\": \"dark\"")).IsTrue();
            await Assert.That(merged.Contains("\"provider\": \"openai\"")).IsTrue();

            // And the merge must remain loadable.
            var reload = await store.LoadAsync();
            await Assert.That(reload.IsSuccess).IsTrue();
            await Assert.That(reload.Value.Theme).IsEqualTo("dark");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
