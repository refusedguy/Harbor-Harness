using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Config.Tests;
/// <summary>
///     Tests for JsonConfigStore — file-based HarborConfig persistence.
/// </summary>
public class JsonConfigStoreTests
{
    private static string NewTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-config-{Guid.NewGuid():N}", "config.json");

    private static string WriteTempConfig(string json)
    {
        string path = NewTempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Test]
    public async Task LoadAsync_ExistingFile_ReturnsDeserializedConfig()
    {
        string json = """
                      {
                        "provider": "anthropic",
                        "model": "anthropic/claude-opus-4",
                        "agent": "code",
                        "tui": "ansi",
                        "storage": "jsonl",
                        "onboarded": true,
                        "apiKeys": {
                          "anthropic": "sk-ant-xxx"
                        },
                        "maxSteps": 30,
                        "costLimit": 5.0
                      }
                      """;
        string path = WriteTempConfig(json);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Provider).IsEqualTo("anthropic");
            await Assert.That(result.Value.Model).IsEqualTo("anthropic/claude-opus-4");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
            await Assert.That(result.Value.Onboarded).IsTrue();
            await Assert.That(result.Value.ApiKeys["anthropic"]).IsEqualTo("sk-ant-xxx");
            await Assert.That(result.Value.MaxSteps).IsEqualTo(30);
            await Assert.That(result.Value.CostLimit).IsEqualTo(5.0m);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_NoFile_ReturnsDefault()
    {
        string path = NewTempConfigPath();
        // Don't create the file — LoadAsync should return Default.
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

        var result = await store.LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        var defaults = HarborConfig.Default;
        await Assert.That(result.Value.Provider).IsEqualTo(defaults.Provider);
        await Assert.That(result.Value.Agent).IsEqualTo(defaults.Agent);
        await Assert.That(result.Value.Onboarded).IsFalse();
        await Assert.That(result.Value.MaxSteps).IsEqualTo(defaults.MaxSteps);
        await Assert.That(result.Value.ApiKeys.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SaveAsync_LoadAsync_Roundtrip()
    {
        string path = NewTempConfigPath();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var config = new HarborConfig
            {
                Provider = "openai",
                Model = "openai/gpt-4o",
                Agent = "plan",
                Tui = "plain",
                Storage = "sqlite",
                Onboarded = true,
                MaxSteps = 25,
                CostLimit = 7.5m,
                ApiKeys = { ["openai"] = "sk-xxx" }
            };

            var saveResult = await store.SaveAsync(config);
            await Assert.That(saveResult.IsSuccess).IsTrue();
            await Assert.That(File.Exists(path)).IsTrue();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.IsSuccess).IsTrue();
            await Assert.That(loaded.Value.Provider).IsEqualTo("openai");
            await Assert.That(loaded.Value.Model).IsEqualTo("openai/gpt-4o");
            await Assert.That(loaded.Value.Agent).IsEqualTo("plan");
            await Assert.That(loaded.Value.Tui).IsEqualTo("plain");
            await Assert.That(loaded.Value.Storage).IsEqualTo("sqlite");
            await Assert.That(loaded.Value.Onboarded).IsTrue();
            await Assert.That(loaded.Value.MaxSteps).IsEqualTo(25);
            await Assert.That(loaded.Value.CostLimit).IsEqualTo(7.5m);
            await Assert.That(loaded.Value.ApiKeys["openai"]).IsEqualTo("sk-xxx");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_ConsoleExSection_ParsesKillSwitchAndSyncUpdates()
    {
        string json = """
                      {
                        "provider": "mock",
                        "model": "mock/test-model",
                        "onboarded": true,
                        "tui": "consoleex",
                        "ui": {
                          "consoleEx": { "enabled": false, "syncUpdates": false }
                        }
                      }
                      """;
        string path = WriteTempConfig(json);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Tui).IsEqualTo("consoleex");
            await Assert.That(result.Value.Ui.ConsoleEx.Enabled).IsFalse();
            await Assert.That(result.Value.Ui.ConsoleEx.SyncUpdates).IsFalse();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task LoadAsync_LegacyRootConsoleEx_StillHonored()
    {
        string json = """
                      {
                        "provider": "mock",
                        "model": "mock/test-model",
                        "onboarded": true,
                        "consoleEx": { "enabled": false, "syncUpdates": false }
                      }
                      """;
        string path = WriteTempConfig(json);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Ui.ConsoleEx.Enabled).IsFalse();
            await Assert.That(result.Value.Ui.ConsoleEx.SyncUpdates).IsFalse();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task LoadAsync_MissingConsoleExSection_AppliesDefaults()
    {
        string json = """
                      {
                        "provider": "mock",
                        "model": "mock/test-model",
                        "onboarded": true
                      }
                      """;
        string path = WriteTempConfig(json);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Ui.ConsoleEx.Enabled).IsTrue();
            await Assert.That(result.Value.Ui.ConsoleEx.SyncUpdates).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task SaveAsync_LoadAsync_Roundtrip_ConsoleExSectionSurvives()
    {
        string path = NewTempConfigPath();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var config = new HarborConfig
            {
                Provider = "mock",
                Model = "mock/test-model",
                Tui = "consoleex",
                Onboarded = true
            };
            config.Ui = config.Ui with { ConsoleEx = new ConsoleExUiConfig(Enabled: false, SyncUpdates: false) };

            var saveResult = await store.SaveAsync(config);
            await Assert.That(saveResult.IsSuccess).IsTrue();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.IsSuccess).IsTrue();
            await Assert.That(loaded.Value.Tui).IsEqualTo("consoleex");
            await Assert.That(loaded.Value.Ui.ConsoleEx.Enabled).IsFalse();
            await Assert.That(loaded.Value.Ui.ConsoleEx.SyncUpdates).IsFalse();

            // Defaults are omitted from the persisted JSON (no config drift for legacy users),
            // and the non-default section is written in the canonical nested `ui` shape
            // (no root-level alias, no explicit nulls).
            string persisted = File.ReadAllText(path);
            await Assert.That(persisted).Contains("\"ui\":{\"consoleEx\":{\"enabled\":false,\"syncUpdates\":false}}");
            await Assert.That(persisted).DoesNotContain("\"consoleEx\":null");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task UpdateAsync_ChangesValue()
    {
        string path = NewTempConfigPath();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            await store.SaveAsync(new HarborConfig { Provider = "anthropic", Agent = "code" });

            var updateResult = await store.UpdateAsync(c =>
            {
                c.Agent = "explore";
                c.MaxSteps = 100;
                return c;
            });
            await Assert.That(updateResult.IsSuccess).IsTrue();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Agent).IsEqualTo("explore");
            await Assert.That(loaded.Value.MaxSteps).IsEqualTo(100);
            // Untouched fields are preserved.
            await Assert.That(loaded.Value.Provider).IsEqualTo("anthropic");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task UpdateAsync_OnMissingFile_LoadsDefaultThenSaves()
    {
        string path = NewTempConfigPath();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var result = await store.UpdateAsync(c =>
            {
                c.Provider = "ollama";
                return c;
            });

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(File.Exists(path)).IsTrue();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Provider).IsEqualTo("ollama");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task GetDefaultPath_ReturnsHarborConfigJson()
    {
        string path = JsonConfigStore.GetDefaultPath();
        await Assert.That(path).Contains(".harbor");
        await Assert.That(Path.GetFileName(path)).IsEqualTo("config.json");
    }
}
