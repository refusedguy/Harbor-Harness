using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     Snapshot slice (#183): <c>HarborConfig.EnabledPlugins</c> / <c>DisabledTools</c>
///     getters return the underlying read-only list directly instead of copying per
///     access, while <c>ToRaw()</c> keeps owning copies for its mutable DTO.
/// </summary>
public class HarborConfigSnapshotTests
{
    [Test]
    public async Task EnabledPlugins_Getter_ReturnsSameSnapshot()
    {
        var config = new HarborConfig();
        config.EnabledPlugins = new List<string> { "alpha", "beta" };

        var first = config.EnabledPlugins;
        var second = config.EnabledPlugins;

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(first[0]).IsEqualTo("alpha");
        await Assert.That(first[1]).IsEqualTo("beta");
    }

    [Test]
    public async Task DisabledTools_Getter_ReturnsSameSnapshot()
    {
        var config = new HarborConfig();
        config.DisabledTools = new List<string> { "bash" };

        var first = config.DisabledTools;
        var second = config.DisabledTools;

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0]).IsEqualTo("bash");
    }

    [Test]
    public async Task PluginLists_Getters_AllocateNothing()
    {
        var config = new HarborConfig();
        config.EnabledPlugins = new List<string> { "alpha" };
        config.DisabledTools = new List<string> { "bash" };

        // Warmup for tier-up.
        for (int i = 0; i < 50; i++)
        {
            _ = config.EnabledPlugins;
            _ = config.DisabledTools;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            _ = config.EnabledPlugins;
            _ = config.DisabledTools;
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0);
    }

    [Test]
    public async Task EnabledPlugins_Setter_RoundTripsContents()
    {
        var config = new HarborConfig();
        config.EnabledPlugins = new List<string> { "alpha", "beta" };

        await Assert.That(config.EnabledPlugins.Count).IsEqualTo(2);

        config.EnabledPlugins = new List<string> { "gamma" };

        await Assert.That(config.EnabledPlugins.Count).IsEqualTo(1);
        await Assert.That(config.EnabledPlugins[0]).IsEqualTo("gamma");
    }

    [Test]
    public async Task ToRaw_OwnsItsLists()
    {
        var config = new HarborConfig();
        config.EnabledPlugins = new List<string> { "alpha" };
        config.DisabledTools = new List<string> { "bash" };

        var raw = config.ToRaw();
        raw.EnabledPlugins!.Add("injected");
        raw.DisabledTools!.Add("injected");

        // Mutating the DTO must not leak back into the live config.
        await Assert.That(config.EnabledPlugins.Count).IsEqualTo(1);
        await Assert.That(config.DisabledTools.Count).IsEqualTo(1);
    }
}
