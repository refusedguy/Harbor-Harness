using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Storage;

namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     #1739: the manifest is DATA, and the host reads it without the plugin's
///     code. These tests cover the read: what a manifest may say, what it may
///     not, and what happens to a field this build does not know.
/// </summary>
public sealed class PluginManifestReaderTests
{
    /// <summary>
    ///     A manifest that says everything it may say is read whole: the identity,
    ///     the compatibility floor, the capabilities and the axes.
    /// </summary>
    [Test]
    public async Task Reader_ReadsEveryDeclaredField()
    {
        var result = PluginManifestReader.Parse(
            """
            {
              "schemaVersion": 1,
              "name": "web-search",
              "version": "1.2.0",
              "harbor": "0.4.0",
              "capabilities": ["http_requests", "read_files"],
              "axes": ["IToolPlugin"]
            }
            """,
            "web-search.harbor-plugin.json");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.SchemaVersion).IsEqualTo(1);
        await Assert.That(result.Value.Name).IsEqualTo("web-search");
        await Assert.That(result.Value.Version).IsEqualTo(new Version(1, 2, 0));
        await Assert.That(result.Value.RequiredHarborVersion).IsEqualTo(new Version(0, 4, 0));
        await Assert.That(result.Value.Capabilities!.Count).IsEqualTo(2);
        await Assert.That(result.Value.Capabilities!.Contains(PluginCapability.HttpRequests)).IsTrue();
        await Assert.That(result.Value.Capabilities!.Contains(PluginCapability.ReadFiles)).IsTrue();
        await Assert.That(result.Value.Axes!.Contains("IToolPlugin")).IsTrue();
    }

    /// <summary>
    ///     A manifest may declare as little as it likes. A manifest that says
    ///     nothing is a valid manifest, and every field it leaves out stays
    ///     unsaid: the host learns nothing extra about the plugin.
    /// </summary>
    [Test]
    public async Task Reader_EmptyManifest_IsValidAndSaysNothing()
    {
        var result = PluginManifestReader.Parse("{}", "empty.harbor-plugin.json");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsNull();
        await Assert.That(result.Value.Version).IsNull();
        await Assert.That(result.Value.RequiredHarborVersion).IsNull();
        await Assert.That(result.Value.Capabilities).IsNull();
        await Assert.That(result.Value.Axes).IsNull();
    }

    /// <summary>
    ///     A field this build does not know is IGNORED, not refused. That is what
    ///     makes a manifest written for a newer Harbor still load here — the
    ///     field is simply not applied.
    /// </summary>
    [Test]
    public async Task Reader_UnknownField_DoesNotBreakTheParse()
    {
        var result = PluginManifestReader.Parse(
            """
            {
              "name": "web-search",
              "icon": "https://example.invalid/icon.png",
              "author": { "name": "someone" },
              "locale": { "en": { "title": "Web search" } }
            }
            """,
            "unknown-fields.harbor-plugin.json");

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("a field this build does not know cannot change what the plugin does, so it is ignored");
        await Assert.That(result.Value.Name).IsEqualTo("web-search");
    }

    /// <summary>
    ///     A capability name the host does not know is a request the host cannot
    ///     honour. The same strict reading the comment directive gets: the
    ///     manifest is refused, and the plugin does not load.
    /// </summary>
    [Test]
    public async Task Reader_UnknownCapability_RefusesTheManifest()
    {
        await AssertRefused(
            """
            {
              "name": "web-search",
              "capabilities": ["http_requests", "teleport"]
            }
            """,
            "teleport");
    }

    /// <summary>
    ///     An extension axis the host does not have is refused, and the message
    ///     names the axes that exist — the list the plugin author has to choose
    ///     from.
    /// </summary>
    [Test]
    public async Task Reader_UnknownAxis_RefusesTheManifestAndNamesTheKnownOnes()
    {
        await AssertRefused(
            """
            {
              "name": "web-search",
              "axes": ["IToolPlugin", "ITelepathyPlugin"]
            }
            """,
            "ITelepathyPlugin");
    }

    /// <summary>
    ///     A manifest format this build does not implement is refused, not
    ///     guessed at. A plugin whose declaration the host cannot read must not
    ///     run.
    /// </summary>
    [Test]
    public async Task Reader_UnknownSchemaVersion_RefusesTheManifest()
    {
        await AssertRefused(
            """
            {
              "schemaVersion": 99,
              "name": "web-search"
            }
            """,
            "99");
    }

    /// <summary>
    ///     A version that is not a version is refused, and the message says what
    ///     a version looks like.
    /// </summary>
    [Test]
    public async Task Reader_MalformedVersion_RefusesTheManifest()
    {
        await AssertRefused(
            """
            {
              "harbor": "not-a-version"
            }
            """,
            "not-a-version");
    }

    /// <summary>
    ///     A document that is not JSON at all is refused with the reason, not
    ///     swallowed.
    /// </summary>
    [Test]
    public async Task Reader_NotJson_RefusesTheManifest()
    {
        var result = PluginManifestReader.Parse("this is not json", "broken.harbor-plugin.json");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("broken.harbor-plugin.json");
    }

    /// <summary>
    ///     A JSON document that is not an object is refused: a manifest is a set
    ///     of named fields, and an array is not that.
    /// </summary>
    [Test]
    public async Task Reader_NotAnObject_RefusesTheManifest()
    {
        var result = PluginManifestReader.Parse("[]", "array.harbor-plugin.json");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("array.harbor-plugin.json");
    }

    /// <summary>
    ///     The manifest belongs to ONE plugin: it is the file next to the source,
    ///     named after it. That is what keeps two plugins in one directory from
    ///     sharing one declaration.
    /// </summary>
    [Test]
    public async Task Reader_ManifestPath_IsNamedAfterTheSource()
    {
        string path = PluginManifestReader.PathFor(Path.Combine("/plugins", "MyPlugin.cs"));

        await Assert.That(Path.GetFileName(path)).IsEqualTo("MyPlugin.harbor-plugin.json");
        await Assert.That(Path.GetDirectoryName(path)).IsEqualTo(Path.Combine("/plugins"));
    }

    /// <summary>
    ///     The content hash is part of the plugin's content hash, so an edited
    ///     manifest is an edited plugin — and the trust decision that was given
    ///     for the old manifest does not carry over.
    /// </summary>
    [Test]
    public async Task Reader_ContentHash_ChangesWithTheManifest()
    {
        var first = PluginManifestReader.Parse("""{"name":"a"}""", "a.harbor-plugin.json");
        var second = PluginManifestReader.Parse("""{"name":"b"}""", "a.harbor-plugin.json");

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(first.Value.ContentHash).IsNotEqualTo(second.Value.ContentHash);
    }

    /// <summary>
    ///     A manifest the host cannot read is refused, and the reason names the
    ///     thing that was wrong.
    /// </summary>
    /// <param name="json">The manifest document.</param>
    /// <param name="expected">The text the refusal must name.</param>
    private static async Task AssertRefused(string json, string expected)
    {
        var result = PluginManifestReader.Parse(json, "manifest.harbor-plugin.json");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(expected);
    }
}
