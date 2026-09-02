using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Integration tests: the real-world sample plugin sources under
///     <c>samples/</c> compile and load through the production
///     <see cref="CsPluginLoader" /> pipeline — not just the inline
///     hello-world fixture. This is the CI guard for "a plugin author who
///     copies a shipped sample always gets a working build".
/// </summary>
public sealed class SamplePluginsIntegrationTests
{
    /// <summary>The five shipped CS plugin sources with the tools they register.</summary>
    private static readonly (string RelativePath, string PluginName, string ToolName)[] Samples =
    {
        ("samples/plugins-cs/HelloWorldPlugin.cs", "hello-world", "hello"),
        ("samples/plugins/Harbor.Plugin.WebSearch/WebSearchPlugin.cs", "websearch", "websearch"),
        ("samples/plugins/Harbor.Plugin.TodoWrite/TodoWritePlugin.cs", "todowrite", "todo"),
        ("samples/plugins/Harbor.Plugin.GitTools/GitToolsPlugin.cs", "gittools", "git"),
        ("samples/plugins/Harbor.Plugin.FileTree/FileTreePlugin.cs", "filetree", "tree"),
    };

    /// <summary>
    ///     Every shipped sample compiles via the real loader path and its tool
    ///     lands in the host registry.
    /// </summary>
    [Test]
    public async Task DiscoverAndLoad_AllShippedSamples_LoadsFivePluginsWithTheirTools()
    {
        string repoRoot = LocateRepoRoot();
        using var fixture = await PluginTestFixture.CreateAsync(uniqueSuffix: "S").ConfigureAwait(false);
        foreach ((string relativePath, _, _) in Samples)
        {
            string source = await File.ReadAllTextAsync(Path.Combine(repoRoot, relativePath)).ConfigureAwait(false);
            await fixture.WritePluginAsync(source, Path.GetFileName(relativePath)).ConfigureAwait(false);
        }

        var host = new FakePluginLoadHost();
        var loader = new CsPluginLoader(
            host,
            NullLogger<CsPluginLoader>.Instance,
            fixture.HarborDir);

        var scripts = await loader.DiscoverScriptsAsync().ConfigureAwait(false);
        if (scripts.Count != Samples.Length)
        {
            Assert.Fail(
                $"Expected {Samples.Length} scripts, discovered {scripts.Count}. " +
                $"PluginsDir contents: [{string.Join(", ", Directory.EnumerateFiles(fixture.PluginsDir))}].");
        }

        var result = await loader.DiscoverAndLoadAsync().ConfigureAwait(false);

        if (!result.IsSuccess || result.Value.Count != Samples.Length)
        {
            // The facade silently SKIPS per-plugin failures (logged only), so
            // re-run every unloaded script through the compiler directly to
            // surface real diagnostics in the assertion message.
            string details = await DescribeFailuresAsync(scripts).ConfigureAwait(false);
            Assert.Fail(
                $"Expected {Samples.Length} loaded plugins, got {(result.IsSuccess ? result.Value.Count : "failure")}." +
                $"\n{details}");
            return;
        }

        // One compiled plugin per sample file, name preserved from source.
        await Assert.That(result.Value.Count).IsEqualTo(Samples.Length);
        foreach ((_, string pluginName, _) in Samples)
        {
            await Assert.That(result.Value.Any(p => p.Name == pluginName)).IsTrue();
        }

        // And every sample's registered tool reached the host registry.
        string[] expectedTools = [.. Samples.Select(s => s.ToolName)];
        var registered = host.RegisteredTools.Select(t => t.Name.Value).ToHashSet(StringComparer.Ordinal);
        foreach (string toolName in expectedTools)
        {
            await Assert.That(registered.Contains(toolName)).IsTrue();
        }
    }

    /// <summary>
    ///     Re-compiles each discovered script through the production compiler
    ///     and returns a human-readable report of every failure (empty string
    ///     when all scripts compile clean).
    /// </summary>
    private static async Task<string> DescribeFailuresAsync(IReadOnlyList<PluginScript> scripts)
    {
        var compiler = new RoslynPluginCompiler(new PluginAssemblyReferences(NullLogger<PluginAssemblyReferences>.Instance));
        var report = new System.Text.StringBuilder();
        foreach (var script in scripts)
        {
            var compiled = await compiler.CompileAsync(script).ConfigureAwait(false);
            if (compiled.IsFailure)
            {
                report.AppendLine($"— {Path.GetFileName(script.Path)}: {compiled.Error}");
            }
        }

        return report.ToString();
    }

    /// <summary>
    ///     Walk up from the test binaries to the repository root (the directory
    ///     that contains <c>samples/plugins-cs</c>).
    /// </summary>
    private static string LocateRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "samples", "plugins-cs");
            if (Directory.Exists(probe))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root with samples/plugins-cs not found above {AppContext.BaseDirectory}.");
    }
}
