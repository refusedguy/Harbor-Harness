using CSharpFunctionalExtensions;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Storage;

namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     #1734: a plugin declares its settings in its manifest, as data. The
///     manifest is read before the plugin is compiled, so the host knows what
///     a plugin wants before the plugin runs.
/// </summary>
public sealed class PluginManifestConfigTests
{
    /// <summary>
    ///     A manifest that declares settings carries the schema: the settings,
    ///     their types, their defaults and their ranges.
    /// </summary>
    [Test]
    public async Task Manifest_WithAConfig_CarriesTheSchema()
    {
        Result<PluginManifest> manifest = PluginManifestReader.Parse(
            """
            {
              "name": "web-search",
              "config": {
                "maxResults": { "type": "integer", "default": 5, "minimum": 1, "maximum": 50 },
                "apiKey": { "type": "string", "secret": true }
              }
            }
            """,
            "web-search.harbor-plugin.json");

        await Assert.That(manifest.IsSuccess).IsTrue();
        await Assert.That(manifest.Value.Config).IsNotNull();
        await Assert.That(manifest.Value.Config!.Fields.Count).IsEqualTo(2);
        await Assert.That(manifest.Value.Config!.Find("maxResults")!.Default!.AsInteger()).IsEqualTo(5L);
        await Assert.That(manifest.Value.Config!.Find("maxResults")!.Maximum).IsEqualTo(50d);
        await Assert.That(manifest.Value.Config!.Find("apiKey")!.IsSecret).IsTrue();
    }

    /// <summary>
    ///     A manifest with no <c>config</c> declares no settings, and the
    ///     plugin is a plugin the host knows nothing extra about.
    /// </summary>
    [Test]
    public async Task Manifest_WithoutAConfig_DeclaresNoSettings()
    {
        Result<PluginManifest> manifest = PluginManifestReader.Parse(
            """{ "name": "web-search" }""",
            "web-search.harbor-plugin.json");

        await Assert.That(manifest.IsSuccess).IsTrue();
        await Assert.That(manifest.Value.Config).IsNull();
    }

    /// <summary>
    ///     A schema the host cannot read refuses the plugin, and the refusal
    ///     names the setting: the plugin shipped a declaration, and the host
    ///     failed to apply it.
    /// </summary>
    [Test]
    public async Task Manifest_WithABrokenConfig_RefusesTheManifest()
    {
        Result<PluginManifest> manifest = PluginManifestReader.Parse(
            """{ "config": { "port": { "type": "interger" } } }""",
            "web-search.harbor-plugin.json");

        await Assert.That(manifest.IsFailure).IsTrue();
        await Assert.That(manifest.Error).Contains("port");
        await Assert.That(manifest.Error).Contains("interger");
    }
}
