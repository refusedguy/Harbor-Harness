using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Slice 3 of #1055: the out-of-process host is the default CS-plugin
///     route, so CLI startup never compiles plugins — not synchronously (slice
///     2 removed the block), not in the background either (slice 3 removed the
///     background in-process load). <c>AddHarbor</c> returns without the
///     plugin tool and the tool never appears in-process afterwards: it is
///     served by <c>harbor-plugins-host</c> over MCP.
/// </summary>
[NotInParallel("hosting")]
public class PluginStartupLoadTests : PluginProbeTestBase
{
    /// <inheritdoc />
    protected override PluginProbe Probe => new("startup-probe", "StartupProbe");

    /// <inheritdoc />
    protected override string TempDirPrefix => "harbor-startup";

    [Test]
    public async Task AddHarbor_WithPreexistingGlobalPlugin_DoesNotBlockOnPluginTools()
    {
        string harborDir = TempHarborDir();
        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, Probe.FileName(suffix)),
            SamplePluginText(suffix));

        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });

        // Startup never compiles: the registry read below observes the final
        // state, not a race with a background load.
        var names = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();
        await Assert.That(names).DoesNotContain($"hello_{suffix}");
    }

    [Test]
    public async Task AddHarbor_WithPreexistingGlobalPlugin_LoadsInBackground()
    {
        string harborDir = TempHarborDir();
        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string toolName = $"hello_{suffix}";
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, Probe.FileName(suffix)),
            SamplePluginText(suffix));

        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });

        // Slice 3 serves plugin tools out-of-process (harbor-plugins-host over
        // MCP 'harbor-csharp-plugins'), so the in-process registry must stay
        // without the tool — and the startup load that used to track the
        // background compile completes empty.
        var loaded = await sp.GetRequiredService<StartupPluginLoad>().Completion
            .WaitAsync(TimeSpan.FromMinutes(2));
        await Assert.That(loaded.Count).IsEqualTo(0);

        bool found = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Any(t => t.Name.Value.Equals(toolName, StringComparison.Ordinal));
        await Assert.That(found).IsEqualTo(false);
    }
}
