using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     #1739: the manifest is read where the plugin files come from, and it
///     travels with the script from there. These tests cover the discovery
///     side: a plugin WITH a manifest, a plugin WITHOUT one, and a manifest
///     that cannot be read.
/// </summary>
public sealed class PluginManifestDiscoveryTests
{
    /// <summary>
    ///     The manifest next to the source is read and attached to the script, so
    ///     the rest of the pipeline never has to go back to the disk for it.
    /// </summary>
    [Test]
    public async Task FileSystemSource_ReadsTheManifestNextToTheSource()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-read").ConfigureAwait(false);
        await fixture.WritePluginAsync("public class A {}", "a.cs").ConfigureAwait(false);
        await fixture.WriteManifestAsync(
            """
            {
              "name": "a-plugin",
              "version": "2.0.0",
              "harbor": "0.4.0",
              "capabilities": ["read_files"],
              "axes": ["IToolPlugin"]
            }
            """,
            "a.cs").ConfigureAwait(false);

        PluginScript script = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await Assert.That(script.Manifest).IsNotNull();
        await Assert.That(script.Manifest!.Name).IsEqualTo("a-plugin");
        await Assert.That(script.Manifest!.Version).IsEqualTo(new Version(2, 0, 0));
        await Assert.That(script.Manifest!.RequiredHarborVersion).IsEqualTo(new Version(0, 4, 0));
        await Assert.That(script.ManifestError).IsNull();
    }

    /// <summary>
    ///     A plugin with no manifest is a plugin the host knows nothing extra
    ///     about, and it is discovered exactly as before: the directive in the
    ///     source still decides the capabilities.
    /// </summary>
    [Test]
    public async Task FileSystemSource_PluginWithoutAManifest_IsDiscoveredAsBefore()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-absent").ConfigureAwait(false);
        await fixture.WritePluginAsync(
            "// harbor:capabilities read_files\npublic class A {}",
            "a.cs").ConfigureAwait(false);

        PluginScript script = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await Assert.That(script.Manifest).IsNull()
            .Because("a plugin without a manifest file has no manifest to read");
        await Assert.That(script.ManifestError).IsNull();
        await Assert.That(script.DeclaredCapabilities.Contains(PluginCapability.ReadFiles)).IsTrue()
            .Because("without a manifest, the comment directive is still the declaration");
    }

    /// <summary>
    ///     A manifest that declares capabilities REPLACES the directive: the
    ///     declaration moves out of the comment and into data.
    /// </summary>
    [Test]
    public async Task FileSystemSource_ManifestCapabilities_ReplaceTheDirective()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-caps").ConfigureAwait(false);
        await fixture.WritePluginAsync(
            "// harbor:capabilities read_files\npublic class A {}",
            "a.cs").ConfigureAwait(false);
        await fixture.WriteManifestAsync(
            """{ "capabilities": ["http_requests"] }""",
            "a.cs").ConfigureAwait(false);

        PluginScript script = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await Assert.That(script.DeclaredCapabilities.Contains(PluginCapability.HttpRequests)).IsTrue();
        await Assert.That(script.DeclaredCapabilities.Contains(PluginCapability.ReadFiles)).IsFalse()
            .Because("the manifest is the declaration when it declares capabilities; the directive is the "
                   + "fallback, not a second declaration");
    }

    /// <summary>
    ///     A manifest that says nothing about capabilities leaves the directive
    ///     in charge. A plugin that ships a manifest for its name does not lose
    ///     the capabilities it declared in the source.
    /// </summary>
    [Test]
    public async Task FileSystemSource_ManifestWithoutCapabilities_LeavesTheDirectiveInCharge()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-nocaps").ConfigureAwait(false);
        await fixture.WritePluginAsync(
            "// harbor:capabilities read_files\npublic class A {}",
            "a.cs").ConfigureAwait(false);
        await fixture.WriteManifestAsync(
            """{ "name": "a-plugin" }""",
            "a.cs").ConfigureAwait(false);

        PluginScript script = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await Assert.That(script.DeclaredCapabilities.Contains(PluginCapability.ReadFiles)).IsTrue();
    }

    /// <summary>
    ///     A manifest that cannot be read is not dropped at discovery: the
    ///     script carries the reason, and the host refuses the plugin with it.
    ///     Dropping it here would make a broken manifest look like a plugin that
    ///     does not exist.
    /// </summary>
    [Test]
    public async Task FileSystemSource_BrokenManifest_CarriesTheReason()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-broken").ConfigureAwait(false);
        await fixture.WritePluginAsync("public class A {}", "a.cs").ConfigureAwait(false);
        await fixture.WriteManifestAsync("""{ "capabilities": ["teleport"] }""", "a.cs").ConfigureAwait(false);

        PluginScript script = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await Assert.That(script.Manifest).IsNull();
        await Assert.That(script.ManifestError).IsNotNull();
        await Assert.That(script.ManifestError!).Contains("teleport");
    }

    /// <summary>
    ///     The manifest is part of the plugin's content, so the content hash
    ///     covers it: the same source with a manifest and without one is two
    ///     different plugins as far as the trust decision and the compile cache
    ///     are concerned.
    /// </summary>
    [Test]
    public async Task FileSystemSource_Manifest_IsPartOfTheContentHash()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-hash").ConfigureAwait(false);
        await fixture.WritePluginAsync("public class A {}", "a.cs").ConfigureAwait(false);
        PluginScript withoutManifest = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await fixture.WriteManifestAsync("""{ "name": "a-plugin" }""", "a.cs").ConfigureAwait(false);
        PluginScript withManifest = await DiscoverSingleAsync(fixture).ConfigureAwait(false);

        await Assert.That(withManifest.Hash).IsNotEqualTo(withoutManifest.Hash)
            .Because("a plugin that ships a declaration is not the same plugin as one that does not");
    }

    private static async Task<PluginScript> DiscoverSingleAsync(PluginTestFixture fixture)
    {
        var source = new FileSystemPluginSource(
            new[] { fixture.PluginsDir },
            NullLogger<FileSystemPluginSource>.Instance);

        var scripts = new List<PluginScript>();
        await foreach (PluginScript script in source.GetScriptsAsync().ConfigureAwait(false))
        {
            scripts.Add(script);
        }

        await Assert.That(scripts.Count).IsEqualTo(1);
        return scripts[0];
    }
}
