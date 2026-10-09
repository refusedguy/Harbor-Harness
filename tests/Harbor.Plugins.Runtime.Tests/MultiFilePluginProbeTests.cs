using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Measurement probe for issue #1046: a plugin split across two
///     cross-referencing <c>.cs</c> files in a plugins subdirectory. Three tests
///     isolate the three layers — discovery, compilation, end-to-end — so a CI run
///     shows exactly which layer drops multi-file plugins instead of guessing.
/// </summary>
public sealed class MultiFilePluginProbeTests
{
    /// <summary>
    ///     Layer 1 (discovery): <see cref="FileSystemPluginSource" /> finds both files
    ///     of a plugin that lives in a subdirectory of the plugins directory.
    /// </summary>
    [Test]
    public async Task Discovery_FindsPluginFilesInSubdirectories()
    {
        using var fixture = await PluginTestFixture.CreateAsync("mfprobe-disc").ConfigureAwait(false);
        await WriteDuoAsync(fixture.PluginsDir).ConfigureAwait(false);

        var source = new FileSystemPluginSource(
            new[] { fixture.PluginsDir },
            NullLogger<FileSystemPluginSource>.Instance);

        var collected = new List<PluginScript>();
        await foreach (var s in source.GetScriptsAsync().ConfigureAwait(false))
            collected.Add(s);

        await Assert.That(collected.Count).IsEqualTo(2);
    }

    /// <summary>
    ///     Layer 2 (compilation): two cross-referencing scripts fed straight into the
    ///     production host pipeline (bypassing discovery) compile jointly and the
    ///     plugin's tool executes with the helper's output.
    /// </summary>
    [Test]
    public async Task Compilation_JointCrossFilePlugin_LoadsThroughHost()
    {
        using var fixture = await PluginTestFixture.CreateAsync("mfprobe-comp").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        (string helperPath, string helperSource, string toolPath, string toolSource) = DuoSources(fixture.PluginsDir);
        var source = new InMemoryPluginSource(new[]
        {
            new PluginScript(helperPath, helperSource),
            new PluginScript(toolPath, toolSource),
        });

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(source, fixture);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("duo-mfprobe");

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "duo_mfprobe");
        ToolResult exec = await ExecuteWithMemoryRetryAsync(tool).ConfigureAwait(false);
        await Assert.That(exec.IsError).IsFalse();
        await Assert.That(exec.Output).Contains("duo-ok");
        await Assert.That(exec.Output).Contains("helper-ok");
    }

    /// <summary>
    ///     Layer 3 (end-to-end): the subdirectory duo loads through the production
    ///     <see cref="CsPluginLoader" /> and its tool executes.
    /// </summary>
    [Test]
    public async Task EndToEnd_SubdirectoryMultiFilePlugin_LoadsAndExecutes()
    {
        using var fixture = await PluginTestFixture.CreateAsync("mfprobe-e2e").ConfigureAwait(false);
        await WriteDuoAsync(fixture.PluginsDir).ConfigureAwait(false);

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);

        var result = await loader.DiscoverAndLoadAsync().ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "duo_mfprobe");
        ToolResult exec = await ExecuteWithMemoryRetryAsync(tool).ConfigureAwait(false);
        await Assert.That(exec.IsError).IsFalse();
        await Assert.That(exec.Output).Contains("duo-ok");
    }

    private static PluginHost BuildHost(InMemoryPluginSource source, PluginTestFixture fixture)
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        return new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                fixture.CacheDir,
                NullLogger<CachingCompiler>.Instance))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(fixture.PluginsDir, NullLogger<PluginRegistrar>.Instance, NullLoggerFactory.Instance),
                NullLogger.Instance))
            .WithOptions(o => o.PluginRoot = fixture.PluginsDir)
            .Build(NullLogger<PluginHost>.Instance);
    }

    private static async Task<(string HelperPath, string HelperSource, string ToolPath, string ToolSource)> WriteDuoAsync(string pluginsDir)
    {
        (string helperPath, string helperSource, string toolPath, string toolSource) = DuoSources(pluginsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        await File.WriteAllTextAsync(helperPath, helperSource).ConfigureAwait(false);
        await File.WriteAllTextAsync(toolPath, toolSource).ConfigureAwait(false);
        return (helperPath, helperSource, toolPath, toolSource);
    }

    private static (string HelperPath, string HelperSource, string ToolPath, string ToolSource) DuoSources(string pluginsDir)
    {
        string dir = Path.Combine(pluginsDir, "duo");
        string helperSource = """
            public static class DuoHelperMfprobe
            {
                public static string Greet() => "helper-ok";
            }
            """;
        string toolSource = """
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

            public sealed class DuoPluginMfprobe : IToolPlugin
            {
                public string Name => "duo-mfprobe";
                public Version Version => new(1, 0, 0);
                public Version RequiredHarborVersion => new(0, 4, 0);
                public string Description => "Multi-file probe plugin (#1046)";

                public void Initialize(PluginContext context)
                {
                }

                public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<DuoToolMfprobe>();

                public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            }

            public sealed class DuoToolMfprobe : ITool
            {
                public ToolName Name => ToolName.Create("duo_mfprobe");
                public string DisplayName => "Duo probe tool";
                public string Description => "Returns a marker via the sibling-file helper";
                public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
                public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
                public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

                public string? PromptSnippet => null;
                public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

                public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
                {
                    return Task.FromResult(ToolResult.Success("duo-ok:" + DuoHelperMfprobe.Greet()));
                }
            }
            """;
        return (Path.Combine(dir, "DuoHelper.cs"), helperSource, Path.Combine(dir, "DuoTool.cs"), toolSource);
    }

    /// <summary>
    ///     Execute a registered tool, tolerating a transient
    ///     <c>[sandbox:memory]</c> block with a bounded retry. Same process-wide
    ///     counter flake as <c>RealWorldPluginsTests.ExecuteThroughSandboxAsync</c>
    ///     (issue #1050): a sibling test's Roslyn compile can exceed the 10 MB budget
    ///     inside the await window under TUnit parallelism.
    /// </summary>
    private static async Task<ToolResult> ExecuteWithMemoryRetryAsync(ITool tool)
    {
        using var doc = JsonDocument.Parse("{}");
        ToolResult result = await tool.ExecuteAsync(doc.RootElement, MakeContext()).ConfigureAwait(false);
        for (int attempt = 1;
            result.IsError
                && result.Output.Contains("[sandbox:memory]", StringComparison.Ordinal)
                && attempt < 5;
            attempt++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            result = await tool.ExecuteAsync(doc.RootElement, MakeContext()).ConfigureAwait(false);
        }

        return result;
    }

    private static ToolContext MakeContext() => new(
        "sess-mfprobe",
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"),
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Deny, false)));
}
