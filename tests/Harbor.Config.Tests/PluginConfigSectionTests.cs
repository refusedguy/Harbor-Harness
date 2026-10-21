using System.Text.Json;
using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Config.Tests;

/// <summary>
///     #1734: the per-plugin configuration sections live in the host's
///     configuration file, under <c>plugins.&lt;plugin-id&gt;</c>. They are
///     stored as the user wrote them, because what a section MEANS is the
///     plugin's schema and the schema travels with the plugin.
/// </summary>
public sealed class PluginConfigSectionTests
{
    /// <summary>
    ///     A section the user wrote is read back as it was written.
    /// </summary>
    [Test]
    public async Task LoadAsync_PluginSection_IsReadBack()
    {
        string json = """
                      {
                        "plugins": {
                          "web-search": {
                            "maxResults": 10,
                            "apiKey": "secret",
                            "tags": ["a", "b"]
                          }
                        }
                      }
                      """;
        string path = WriteTempConfig(json);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var result = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Plugins.Count).IsEqualTo(1);
            JsonElement section = result.Value.Plugins["web-search"];
            await Assert.That(section.GetProperty("maxResults").GetInt32()).IsEqualTo(10);
            await Assert.That(section.GetProperty("apiKey").GetString()).IsEqualTo("secret");
            await Assert.That(section.GetProperty("tags").GetArrayLength()).IsEqualTo(2);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     A section survives a save: what the host stores is what the user
    ///     wrote, so writing the configuration back does not lose it.
    /// </summary>
    [Test]
    public async Task SaveAsync_PluginSection_SurvivesTheRoundTrip()
    {
        string path = WriteTempConfig("""
                                      {
                                        "plugins": {
                                          "web-search": { "maxResults": 10 }
                                        }
                                      }
                                      """);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.IsSuccess).IsTrue();

            var saved = await store.SaveAsync(loaded.Value);
            await Assert.That(saved.IsSuccess).IsTrue();

            var reloaded = await store.LoadAsync();
            await Assert.That(reloaded.IsSuccess).IsTrue();
            await Assert.That(reloaded.Value.Plugins.Count).IsEqualTo(1);
            await Assert.That(reloaded.Value.Plugins["web-search"].GetProperty("maxResults").GetInt32())
                .IsEqualTo(10);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     A configuration with no plugin sections has none, and writes none:
    ///     the file stays free of a section nobody uses.
    /// </summary>
    [Test]
    public async Task SaveAsync_NoPluginSections_WritesNoPluginSection()
    {
        string path = WriteTempConfig("{}");
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var saved = await store.SaveAsync(HarborConfig.Default);
            await Assert.That(saved.IsSuccess).IsTrue();

            string text = await File.ReadAllTextAsync(path);
            await Assert.That(text).DoesNotContain("plugins");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     A plugin id with a dot in it would make
    ///     <c>plugins.a.b</c> ambiguous between a plugin called <c>a.b</c> and
    ///     a setting called <c>b</c> of a plugin called <c>a</c>, so the
    ///     configuration is refused and the message names the id.
    /// </summary>
    [Test]
    public async Task LoadAsync_PluginIdWithADot_IsRefusedAndNamesTheId()
    {
        string path = WriteTempConfig("""
                                      {
                                        "plugins": {
                                          "a.b": { "x": 1 }
                                        }
                                      }
                                      """);
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var result = await store.LoadAsync();

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("a.b");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static string NewTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-config-{Guid.NewGuid():N}", "config.json");

    private static string WriteTempConfig(string json)
    {
        string path = NewTempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }
}
