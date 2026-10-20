using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Harbor.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Slice 3 of #1055 (default out-of-proc route): CS plugins must compile and
///     execute in <c>harbor-plugins-host</c>, never in the CLI process. The CLI
///     startup therefore registers nothing in-process — not even in the
///     background — and reports the out-of-proc route in one line. The tools
///     arrive over MCP (<c>harbor-csharp-plugins</c>), not through the
///     <c>IToolRegistry</c>.
/// </summary>
[NotInParallel("hosting")]
public class PluginOutOfProcDefaultTests : PluginAbsenceTestBase
{
    /// <inheritdoc />
    protected override string TempDirPrefix => "harbor-outofproc";

    /// <summary>
    ///     #1055 slice 3: scripts on disk must NOT register tools in the CLI
    ///     process. The startup load completes empty and the route is reported
    ///     in one out-of-proc line; the tool is served by the host over MCP.
    /// </summary>
    [Test]
    public async Task ScriptsPresent_StartupLoadCompletesEmpty_OutOfProcRouteReported()
    {
        string harborDir = TempHarborDir();
        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, "outofproc-probe.cs"),
            ProbeSource).ConfigureAwait(false);

        var capture = new CapturingLoggerFactory();
        using var _cwd = WithTempCwd(); // empty project scope
        using ServiceProvider sp = Compose(harborDir, capture);

        var loaded = await sp.GetRequiredService<StartupPluginLoad>().Completion
            .WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await Assert.That(loaded.Count).IsEqualTo(0)
            .Because("slice 3: the CLI never compiles CS plugins in-process — "
                + "not synchronously, not in the background");

        var toolNames = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();
        await Assert.That(toolNames.Contains("outofproc_s3_probe")).IsFalse()
            .Because("slice 3: plugin tools arrive over MCP from harbor-plugins-host, "
                + "never through the in-process IToolRegistry");

        var routeLines = capture.Entries
            .Where(e => e.Message.Contains("out-of-proc", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(routeLines.Length).IsEqualTo(1)
            .Because("the default route reports itself in exactly one line");
    }

    /// <summary>
    ///     #1055 slice 3: the CLI assembly must not reference the in-process
    ///     compile pipeline at all — no edge to compile means no Roslyn in
    ///     the process, whatever the ambient load state. The host-binary
    ///     probe seam (<c>Harbor.Plugins.Hosting</c>) stays.
    /// </summary>
    [Test]
    public async Task CliComposition_DoesNotReferenceInProcCompilePipeline()
    {
        var referenced = typeof(HarborCompositionContext).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string assembly in new[]
                 {
                     "Harbor.Plugins.Storage",
                     "Harbor.Plugins.Compilation",
                     "Harbor.Plugins.Instantiation",
                     "Harbor.Plugins.Registration",
                 })
        {
            await Assert.That(referenced.Contains(assembly)).IsFalse()
                .Because("slice 3: " + assembly + " is the in-process compile pipeline — "
                    + "the CLI must not reference it; CS plugins compile in harbor-plugins-host");
        }

        await Assert.That(referenced.Contains("Harbor.Plugins.Hosting")).IsTrue()
            .Because("the host-binary probe and the filesystem watcher stay in-process");
    }

    private const string ProbeSource = """
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

        public sealed class OutOfProcS3ProbePlugin : IToolPlugin
        {
            public string Name => "outofproc-s3-probe";
            public Version Version => new(1, 0, 0);
            public Version RequiredHarborVersion => new(0, 4, 0);
            public string Description => "Slice-3 out-of-proc probe plugin";

            public void Initialize(PluginContext context)
            {
            }

            public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<OutOfProcS3ProbeTool>();

            public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        public sealed class OutOfProcS3ProbeTool : ITool
        {
            public ToolName Name => ToolName.Create("outofproc_s3_probe");
            public string DisplayName => "Out Of Proc S3 Probe";
            public string Description => "Returns a greeting";
            public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
            public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
            public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

            public string? PromptSnippet => null;
            public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

            public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ToolResult.Success("Hello from outofproc-s3!"));
            }
        }
        """;
}
