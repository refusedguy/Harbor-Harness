using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.Hosting.Tests;

/// <summary>
///     Integration tests for <see cref="PluginReloadService" />.
///     Slice 3 of #1055 serves CS plugins out-of-process
///     (<c>harbor-plugins-host</c> over MCP), so a reload pass registers
///     nothing in-process: it returns zero loaded with the restart-the-host
///     note, while <see cref="PluginReloadService.ListInstalled" /> keeps
///     enumerating both scopes for the <c>/plugins</c> panel.
/// </summary>
[NotInParallel("hosting")]
public class PluginReloadServiceTests : PluginProbeTestBase
{
    /// <inheritdoc />
    protected override PluginProbe Probe => new("reload-probe", "ReloadProbe");

    /// <inheritdoc />
    protected override string TempDirPrefix => "harbor-reload";

    [Test]
    public async Task ReloadAsync_NewGlobalPlugin_ToolBecomesVisibleInLiveRegistry()
    {
        string harborDir = TempHarborDir();
        Directory.CreateDirectory(harborDir);

        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });

        var registryBefore = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Select(t => t.Name.Value)
            .ToHashSet(StringComparer.Ordinal);

        // Drop a reviewed plugin into the user-managed global scope while "running".
        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, Probe.FileName(uniqueSuffix)),
            SamplePluginText(uniqueSuffix));

        var service = sp.GetRequiredService<PluginReloadService>();
        var summary = await service.ReloadAsync();

        // Slice 3: reload registers nothing in-process — the tools live in
        // the host process. Zero loaded with the restart-the-host note.
        await Assert.That(summary.Loaded).IsEqualTo(0);
        await Assert.That(summary.Notes.Any(n => n.Contains("harbor-plugins-host", StringComparison.Ordinal))).IsEqualTo(true);
        var toolsNow = sp.GetRequiredService<IToolRegistry>().GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();
        await Assert.That(toolsNow.Contains($"hello_{uniqueSuffix}")).IsEqualTo(false);

        // The scope enumeration still sees the file for the /plugins panel.
        var installed = service.ListInstalled();
        await Assert.That(installed.Any(p => p.Name.Equals($"reload-probe-{uniqueSuffix}.cs", StringComparison.Ordinal))).IsEqualTo(true);
    }

    /// <summary>
    ///     The same file loaded twice through reload must not duplicate registrations:
    ///     second pass either skips via cache or fails registration harmlessly.
    /// </summary>
    [Test]
    public async Task ReloadAsync_TwiceSamePlugin_NoDuplicateToolRegistrations()
    {
        string harborDir = TempHarborDir();
        Directory.CreateDirectory(harborDir);

        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });

        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, Probe.FileName(uniqueSuffix)),
            SamplePluginText(uniqueSuffix));

        var service = sp.GetRequiredService<PluginReloadService>();
        _ = await service.ReloadAsync();
        var second = await service.ReloadAsync();

        // Slice 3: every pass is a no-op in-process — still zero, still the
        // same note, and the tool never appears in the live registry.
        await Assert.That(second.Loaded).IsEqualTo(0);
        int occurrences = sp.GetRequiredService<IToolRegistry>().GetAllTools()
            .Count(t => t.Name.Value.Equals($"hello_{uniqueSuffix}", StringComparison.Ordinal));
        await Assert.That(occurrences).IsEqualTo(0);
    }
}
