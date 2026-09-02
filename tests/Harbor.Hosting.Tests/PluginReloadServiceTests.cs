using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.Hosting.Tests;

/// <summary>
///     Integration tests for <see cref="PluginReloadService" /> — a real end-to-end
///     reload: sample plugin dropped into the global scope, hot-loaded through the
///     live composition graph, tool visible in the singleton IToolRegistry.
/// </summary>
public class PluginReloadServiceTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-reload-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }

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
            Path.Combine(pluginsDir, $"reload-probe-{uniqueSuffix}.cs"),
            SamplePluginText(uniqueSuffix));

        var service = sp.GetRequiredService<PluginReloadService>();
        var summary = await service.ReloadAsync();

        await Assert.That(summary.Loaded).IsGreaterThanOrEqualTo(1);
        var toolsNow = sp.GetRequiredService<IToolRegistry>().GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();
        await Assert.That(toolsNow).Contains($"hello_{uniqueSuffix}");
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
            Path.Combine(pluginsDir, $"reload-probe-{uniqueSuffix}.cs"),
            SamplePluginText(uniqueSuffix));

        var service = sp.GetRequiredService<PluginReloadService>();
        _ = await service.ReloadAsync();
        _ = await service.ReloadAsync();

        int occurrences = sp.GetRequiredService<IToolRegistry>().GetAllTools()
            .Count(t => t.Name.Value.Equals($"hello_{uniqueSuffix}", StringComparison.Ordinal));
        await Assert.That(occurrences).IsEqualTo(1);
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
                                                             using Harbor.Abstractions.Plugins;
                                                             using Harbor.Abstractions.Tools;
                                                             using Microsoft.Extensions.Logging;

                                                             public sealed class ReloadProbePlugin{{suffix}} : IToolPlugin
                                                             {
                                                                 public string Name => "reload-probe-{{suffix}}";
                                                                 public Version Version => new(1, 0, 0);
                                                                 public Version RequiredHarborVersion => new(0, 4, 0);
                                                                 public string Description => "Reload probe {{suffix}}";
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
                                                                 public string? PromptSnippet => null;
                                                                 public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
                                                                 public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
                                                                     => Task.FromResult(ToolResult.Success("Hello from probe!"));
                                                             }
                                                             """;
}
