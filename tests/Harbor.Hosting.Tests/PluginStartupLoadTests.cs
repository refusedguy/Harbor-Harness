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
public class PluginStartupLoadTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-startup-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task AddHarbor_WithPreexistingGlobalPlugin_DoesNotBlockOnPluginTools()
    {
        string harborDir = TempHarborDir();
        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, $"startup-probe-{suffix}.cs"),
            SamplePluginText(suffix));

        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });
        using var sp = services.BuildServiceProvider();

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
            Path.Combine(pluginsDir, $"startup-probe-{suffix}.cs"),
            SamplePluginText(suffix));

        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });
        using var sp = services.BuildServiceProvider();

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

    private static string SamplePluginText(string suffix) => $$"""
                                                              using System;
                                                              using System.Collections.Generic;
                                                              using System.Text.Json;
                                                              using System.Threading;
                                                              using System.Threading.Tasks;
                                                              using CSharpFunctionalExtensions;
                                                              using Harbor.Abstractions.Models;
                                                              using Harbor.Abstractions.Models.Identifiers;
                                                              using Harbor.Abstractions.Permissions;
                                                              using Harbor.Abstractions.Plugins;
                                                              using Harbor.Abstractions.Tools;
                                                              using Microsoft.Extensions.Logging;

                                                              public sealed class StartupProbePlugin{{suffix}} : IToolPlugin
                                                              {
                                                                  public string Name => "startup-probe-{{suffix}}";
                                                                  public Version Version => new(1, 0, 0);
                                                                  public Version RequiredHarborVersion => new(0, 4, 0);
                                                                  public string Description => "Startup probe {{suffix}}";
                                                                  public void Initialize(PluginContext context) { }
                                                                  public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<ProbeTool{{suffix}}>();
                                                                  public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
                                                              }

                                                              public sealed class ProbeTool{{suffix}} : ITool
                                                              {
                                                                  public ToolName Name => ToolName.Create("hello_{{suffix}}");
                                                                  public string DisplayName => "Probe {{suffix}}";
                                                                  public string Description => "Returns a greeting";
                                                                  public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
                                                                  public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
                                                                  public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

                                                                  public string? PromptSnippet => null;
                                                                  public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
                                                                  public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
                                                                      => Task.FromResult(ToolResult.Success("Hello from probe!"));
                                                              }
                                                              """;
}
