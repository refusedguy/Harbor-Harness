using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Slice 2 of #1055: CLI startup must not block on plugins.
///     A plugin present BEFORE composition is discovered trust-gated synchronously
///     (fast local I/O — keeps the interactive approval venue) but compiled +
///     registered in the background, so <c>AddHarbor</c> returns without its tool
///     and the tool appears later without any explicit reload.
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

        // The background compile cannot have finished yet: a cold Roslyn
        // JIT + emit takes orders of magnitude longer than the registry
        // read below. On the old sync path this fails (tool already here).
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

        // Safety net: lazy must not mean lost. Poll the LIVE registry —
        // immediate on the old path, eventual on the background path.
        bool found = false;
        for (int i = 0; i < 240 && !found; i++)
        {
            found = sp.GetRequiredService<IToolRegistry>()
                .GetAllTools()
                .Any(t => t.Name.Value.Equals(toolName, StringComparison.Ordinal));
            if (!found)
                await Task.Delay(500);
        }

        await Assert.That(found).IsEqualTo(true);
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
