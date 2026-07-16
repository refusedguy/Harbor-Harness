using Harbor.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Config.Tests;

/// <summary>
/// Tests for JsonConfigStore — file-based HarborConfig persistence.
/// </summary>
public class JsonConfigStoreTests
{
    private static string NewTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-config-{Guid.NewGuid():N}", "config.json");

    private static string WriteTempConfig(string json)
    {
        var path = NewTempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Test]
    public async Task LoadAsync_ExistingFile_ReturnsDeserializedConfig()
    {
        var json = """
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
        var path = WriteTempConfig(json);
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
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_NoFile_ReturnsDefault()
    {
        var path = NewTempConfigPath();
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
        var path = NewTempConfigPath();
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
                ApiKeys = { ["openai"] = "sk-xxx" },
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
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task UpdateAsync_ChangesValue()
    {
        var path = NewTempConfigPath();
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
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task UpdateAsync_OnMissingFile_LoadsDefaultThenSaves()
    {
        var path = NewTempConfigPath();
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
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task GetDefaultPath_ReturnsHarborConfigJson()
    {
        var path = JsonConfigStore.GetDefaultPath();
        await Assert.That(path).Contains(".harbor");
        await Assert.That(Path.GetFileName(path)).IsEqualTo("config.json");
    }
}
