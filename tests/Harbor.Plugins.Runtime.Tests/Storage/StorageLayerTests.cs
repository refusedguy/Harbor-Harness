using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     Tests for the Storage layer: <see cref="InMemoryPluginSource" />,
/// <see cref="FileSystemPluginSource" />, <see cref="CompositePluginSource" />.
/// </summary>
public sealed class StorageLayerTests
{
    /// <summary>
    ///     Test 1 — <see cref="InMemoryPluginSource" /> yields exactly the scripts that
    ///     were added, in insertion order.
    /// </summary>
    [Test]
    public async Task InMemorySource_YieldsAddedScriptsInOrder()
    {
        var source = new InMemoryPluginSource();
        source.Add("a.cs", "public class A {}");
        source.Add("b.cs", "public class B {}");

        var collected = new List<PluginScript>();
        await foreach (var s in source.GetScriptsAsync().ConfigureAwait(false))
            collected.Add(s);

        await Assert.That(collected.Count).IsEqualTo(2);
        await Assert.That(collected[0].Path).IsEqualTo("a.cs");
        await Assert.That(collected[1].Path).IsEqualTo("b.cs");
        // Hashes should be non-empty and distinct (different content).
        await Assert.That(collected[0].Hash).IsNotEqualTo(collected[1].Hash);
    }

    /// <summary>
    ///     Test 2 — <see cref="FileSystemPluginSource" /> discovers .cs files in the
    ///     configured directories and skips directories that don't exist (no exception).
    /// </summary>
    [Test]
    public async Task FileSystemSource_DiscoverCsFiles_SkipsMissingDirectories()
    {
        using var fixture = await PluginTestFixture.CreateAsync("fs").ConfigureAwait(false);
        await fixture.WritePluginAsync("public class A {}", "a.cs").ConfigureAwait(false);
        await fixture.WritePluginAsync("public class B {}", "b.cs").ConfigureAwait(false);

        var source = new FileSystemPluginSource(
            new[] { fixture.PluginsDir, "/nonexistent/dir/that/should/be/skipped" },
            NullLogger<FileSystemPluginSource>.Instance);

        var collected = new List<PluginScript>();
        await foreach (var s in source.GetScriptsAsync().ConfigureAwait(false))
            collected.Add(s);

        await Assert.That(collected.Count).IsEqualTo(2);
        await Assert.That(collected[0].Path.EndsWith("a.cs")).IsTrue();
        await Assert.That(collected[1].Path.EndsWith("b.cs")).IsTrue();
    }

    /// <summary>
    ///     Test 3 — <see cref="FileSystemPluginSource" /> de-duplicates files when the
    ///     same directory is supplied twice.
    /// </summary>
    [Test]
    public async Task FileSystemSource_DedupsOverlappingDirectories()
    {
        using var fixture = await PluginTestFixture.CreateAsync("fs-dedupe").ConfigureAwait(false);
        await fixture.WritePluginAsync("public class A {}", "only.cs").ConfigureAwait(false);

        var source = new FileSystemPluginSource(
            new[] { fixture.PluginsDir, fixture.PluginsDir },
            NullLogger<FileSystemPluginSource>.Instance);

        var collected = new List<PluginScript>();
        await foreach (var s in source.GetScriptsAsync().ConfigureAwait(false))
            collected.Add(s);

        await Assert.That(collected.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     Test 4 — <see cref="CompositePluginSource" /> concatenates sub-sources in
    ///     registration order.
    /// </summary>
    [Test]
    public async Task CompositeSource_ConcatenatesSubsourcesInOrder()
    {
        var first = new InMemoryPluginSource();
        first.Add("first.cs", "public class First {}");
        var second = new InMemoryPluginSource();
        second.Add("second.cs", "public class Second {}");

        var composite = new CompositePluginSource(first, second);

        var collected = new List<PluginScript>();
        await foreach (var s in composite.GetScriptsAsync().ConfigureAwait(false))
            collected.Add(s);

        await Assert.That(collected.Count).IsEqualTo(2);
        await Assert.That(collected[0].Path).IsEqualTo("first.cs");
        await Assert.That(collected[1].Path).IsEqualTo("second.cs");
    }

    /// <summary>
    ///     Test 5 — <see cref="InMemoryPluginSource" /> yields an empty stream when no
    ///     scripts were added (no exception).
    /// </summary>
    [Test]
    public async Task InMemorySource_Empty_YieldsNoScripts()
    {
        var source = new InMemoryPluginSource();
        var count = 0;
        await foreach (var _ in source.GetScriptsAsync().ConfigureAwait(false))
            count++;
        await Assert.That(count).IsEqualTo(0);
    }
}
