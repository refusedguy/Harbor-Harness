using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Shared fixture for the plugin-lifecycle suites (issue #1055, slice 3):
///     a CS-source plugin sits on disk and the CLI must serve it OUT OF PROCESS
///     — startup never compiles it in-process, and a reload pass never
///     registers it in-process either. The invariant is the same everywhere:
///     drop a reviewed <c>.cs</c> plugin into the harbor dir's
///     <c>plugins</c> folder, compose the hosting stack over a memory store,
///     and read the tool registry. What varies is WHICH probe is written (its
///     id and class name) and the temp-root prefix.
///     <para>
///         It lives here and NOT in <c>Harbor.TestKit</c>: it composes
///         <c>Harbor.Hosting</c>, and a TestKit-hosted base would push the
///         hosting assembly into the closure of every TestKit consumer (the
///         epic's rule 3).
///     </para>
/// </summary>
public abstract class PluginProbeTestBase
{
    /// <summary>
    ///     Identity of the CS-source probe plugin this suite drops on disk.
    /// </summary>
    protected sealed record PluginProbe(string Id, string ClassPrefix)
    {
        /// <summary>The file name the probe is written under.</summary>
        public string FileName(string suffix) => $"{Id}-{suffix}.cs";
    }

    /// <summary>The probe plugin this suite writes to disk.</summary>
    protected abstract PluginProbe Probe { get; }

    /// <summary>
    ///     Temp-root prefix for this suite — one per suite, so two suites never
    ///     share a directory (they run in the same process).
    /// </summary>
    protected abstract string TempDirPrefix { get; }

    /// <summary>A fresh harbor dir (created by the caller).</summary>
    protected string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Composes the hosting stack over a memory store.</summary>
    protected static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     A reviewed CS-source plugin that registers one probe tool. The
    ///     in-process route never compiles it (slice 3), so the source only has
    ///     to be plausible — but it is the real thing, not a stub.
    /// </summary>
    protected string SamplePluginText(string suffix) => $$"""
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

        public sealed class {{Probe.ClassPrefix}}Plugin{{suffix}} : IToolPlugin
        {
            public string Name => "{{Probe.Id}}-{{suffix}}";
            public Version Version => new(1, 0, 0);
            public Version RequiredHarborVersion => new(0, 4, 0);
            public string Description => "{{Probe.ClassPrefix}} probe {{suffix}}";
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
