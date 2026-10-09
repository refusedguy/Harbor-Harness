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
///     Subdirectory plugins (issue #1046): <see cref="FileSystemPluginSource" />
///     discovers <c>.cs</c> files at any depth, and a self-contained plugin filed
///     into a subdirectory loads end-to-end. The single-file contract still holds —
///     files that reference each other's types fail compilation loudly (CS0246)
///     instead of vanishing silently at discovery; joint per-directory
///     compilation stays a <c>#422</c> follow-up decision.
/// </summary>
public sealed class SubdirectoryPluginTests
{
    /// <summary>
    ///     Discovery finds both files of a plugin that lives in a subdirectory of
    ///     the plugins directory (was: silently skipped, 0 of 2).
    /// </summary>
    [Test]
    public async Task Discovery_FindsPluginFilesInSubdirectories()
    {
        using var fixture = await PluginTestFixture.CreateAsync("subdir-disc").ConfigureAwait(false);
        await WriteDuoAsync(fixture.PluginsDir).ConfigureAwait(false);

        var collected = await CollectAsync(fixture.PluginsDir).ConfigureAwait(false);

        await Assert.That(collected.Count).IsEqualTo(2);
    }

    /// <summary>
    ///     A self-contained single-file plugin in a subdirectory loads through the
    ///     production <see cref="CsPluginLoader" /> and its tool executes.
    /// </summary>
    [Test]
    public async Task SingleFilePluginInSubdirectory_LoadsAndExecutes()
    {
        using var fixture = await PluginTestFixture.CreateAsync("subdir-single").ConfigureAwait(false);
        string sub = Path.Combine(fixture.PluginsDir, "organized");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(
            Path.Combine(sub, "HelloSubdir.cs"),
            SamplePluginSource.HelloWorld("Subdir")).ConfigureAwait(false);

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);

        var result = await loader.DiscoverAndLoadAsync().ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("hello-world-subdir");

        ITool tool = host.RegisteredTools.Single(t => t.Name.Value == "hello_subdir");
        ToolResult exec = await ExecuteWithMemoryRetryAsync(tool).ConfigureAwait(false);
        await Assert.That(exec.IsError).IsFalse();
        await Assert.That(exec.Output).Contains("Hello from Subdir!");
    }

    /// <summary>
    ///     Two cross-referencing files in a subdirectory are discovered (2 scripts)
    ///     but still compile in isolation, so the run fails loudly with the Roslyn
    ///     CS0246 diagnostic instead of loading nothing with no explanation.
    ///     Guards both directions: a regression to silent skipping breaks the
    ///     count assertion, an unreviewed joint-compilation change breaks the
    ///     failure assertion (its cache-key and capability-union semantics need
    ///     their own design, see #1046).
    /// </summary>
    [Test]
    public async Task CrossFileReferences_FailLoudlyWithCs0246()
    {
        using var fixture = await PluginTestFixture.CreateAsync("subdir-loud").ConfigureAwait(false);
        await WriteDuoAsync(fixture.PluginsDir).ConfigureAwait(false);

        var collected = await CollectAsync(fixture.PluginsDir).ConfigureAwait(false);
        await Assert.That(collected.Count).IsEqualTo(2);

        var source = new InMemoryPluginSource(collected);
        var pluginHost = BuildHost(source, fixture, continueOnError: false);

        var result = await pluginHost.LoadAllAsync(new FakePluginLoadHost()).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("CS0246");
    }

    private static async Task<List<PluginScript>> CollectAsync(string pluginsDir)
    {
        var source = new FileSystemPluginSource(
            new[] { pluginsDir },
            NullLogger<FileSystemPluginSource>.Instance);

        var collected = new List<PluginScript>();
        await foreach (var s in source.GetScriptsAsync().ConfigureAwait(false))
            collected.Add(s);
        return collected;
    }

    private static PluginHost BuildHost(InMemoryPluginSource source, PluginTestFixture fixture, bool continueOnError)
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
            .WithOptions(o =>
            {
                o.PluginRoot = fixture.PluginsDir;
                o.ContinueOnError = continueOnError;
            })
            .Build(NullLogger<PluginHost>.Instance);
    }

    private static async Task WriteDuoAsync(string pluginsDir)
    {
        (string helperPath, string helperSource, string toolPath, string toolSource) = DuoSources(pluginsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        await File.WriteAllTextAsync(helperPath, helperSource).ConfigureAwait(false);
        await File.WriteAllTextAsync(toolPath, toolSource).ConfigureAwait(false);
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
        "sess-subdir",
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"),
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Deny, false)));
}
