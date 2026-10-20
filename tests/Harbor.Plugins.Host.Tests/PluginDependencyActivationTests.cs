using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Host;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Host.Tests;

/// <summary>
///     The out-of-process host's half of the deferred activation (#1733): the host
///     publishes ONE capability — <c>mcp-session</c> — and it becomes true when the MCP
///     client completes the handshake. A plugin that declares it waits for that, and comes
///     up the moment the handshake lands.
/// </summary>
/// <remarks>
///     The mechanism itself (a pending plugin, an activation pass, a cycle) is proved in
///     <c>tests/Harbor.Plugins.Runtime.Tests/Hosting/PluginDependencyTests.cs</c>. This file
///     proves the WIRING: the session flag, the capability, and the trigger that turns them
///     into a tool the client can call.
/// </remarks>
public sealed class PluginDependencyActivationTests
{
    private static McpPluginLoadHost CreateLoadHost() =>
        new(NullLoggerFactory.Instance, new NullEventBus());

    [Test]
    public async Task Initialize_ActivatesThePluginThatWaitedForTheSession()
    {
        var loadHost = CreateLoadHost();

        var dependencies = new PluginDependencyResolver(
            new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
            {
                ["mcp-session"] = () => loadHost.IsSessionEstablished
            });

        var plugin = new SessionWaitingPlugin();
        var loaded = new LoadedPlugin(
            plugin,
            plugin.Name,
            plugin.Version,
            plugin.GetType(),
            "session-waiting.cs",
            "hash",
            false);

        var pluginHost = new PluginHostBuilder()
            .WithSource(new InMemoryPluginSource(
                new[] { new PluginScript("session-waiting.cs", "// inline") }))
            .WithCompiler(new FixedCompiler())
            .WithInstantiator(new FixedInstantiator(loaded))
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(
                    Path.Combine(Path.GetTempPath(), "harbor-plugins-host-tests"),
                    NullLogger<PluginRegistrar>.Instance,
                    NullLoggerFactory.Instance),
                NullLogger.Instance))
            .WithOptions(o => o.Dependencies = dependencies)
            .Build(NullLogger<PluginHost>.Instance);

        var load = await pluginHost.LoadAllAsync(loadHost).ConfigureAwait(false);

        await Assert.That(load.IsSuccess).IsTrue();
        await Assert.That(loadHost.Tools.Count).IsEqualTo(0)
            .Because("the MCP session is not established yet, so the plugin waits");
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(1);

        var server = new McpStdioServer(
            loadHost,
            NullLogger<McpStdioServer>.Instance,
            () => pluginHost.ActivatePending(loadHost));

        using var initialize = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");

        await Assert.That(initialize.RootElement.GetProperty("result").GetProperty("serverInfo")
            .GetProperty("name").GetString()).IsEqualTo("harbor-csharp-plugins");

        await Assert.That(loadHost.Tools.Count).IsEqualTo(1)
            .Because("the handshake is the moment the session becomes true, and the plugin that "
                   + "waited for it comes up with it");
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(0);

        using var list = await CallAsync(server, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await Assert.That(list.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength())
            .IsEqualTo(1)
            .Because("the activated plugin's tool is served to the client");
    }

    [Test]
    public async Task SessionEstablished_IsFalseUntilTheHandshake()
    {
        var loadHost = CreateLoadHost();

        await Assert.That(loadHost.IsSessionEstablished).IsFalse();

        var server = new McpStdioServer(loadHost, NullLogger<McpStdioServer>.Instance);
        _ = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""")
            .ConfigureAwait(false);

        await Assert.That(loadHost.IsSessionEstablished).IsTrue();
    }

    private static async Task<JsonDocument> CallAsync(McpStdioServer server, string request)
    {
        string? response = await server.HandleLineAsync(request, CancellationToken.None).ConfigureAwait(false);
        await Assert.That(response is not null).IsTrue()
            .Because("every request in this suite is addressed — a missing frame is a bug, not a protocol answer");
        return JsonDocument.Parse(response!);
    }

    /// <summary>
    ///     A plugin that declares the <c>mcp-session</c> dependency and contributes one tool.
    /// </summary>
    private sealed class SessionWaitingPlugin : IToolPlugin
    {
        public string Name => "session-waiting";
        public Version Version => new(1, 0, 0);
        public Version RequiredHarborVersion => new(0, 4, 0);
        public string Description => "Waits for the MCP session before it comes up";
        public IReadOnlyList<string> Dependencies => new[] { "mcp-session" };

        public void Initialize(PluginContext context)
        {
        }

        public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool(new SessionTool());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SessionTool : ITool
    {
        public ToolName Name => ToolName.Create("session_tool");
        public string DisplayName => "Session tool";
        public string Description => "Only reachable once the session is up";
        public JsonDocument ParameterSchema => JsonDocument.Parse("""{"type":"object"}""");
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("session"));
    }

    /// <summary>
    ///     A compiler that hands back the test assembly: this suite is about the WIRING, not
    ///     about Roslyn.
    /// </summary>
    private sealed class FixedCompiler : IPluginCompiler
    {
        public Task<Result<CompiledPluginAssembly>> CompileAsync(
            PluginScript script,
            CancellationToken ct = default)
            => Task.FromResult(Result.Success(new CompiledPluginAssembly(
                typeof(FixedCompiler).Assembly,
                "hash",
                script.Path)));
    }

    /// <summary>
    ///     An instantiator that hands back one prepared plugin.
    /// </summary>
    private sealed class FixedInstantiator : IPluginInstantiator
    {
        private readonly LoadedPlugin _plugin;

        internal FixedInstantiator(LoadedPlugin plugin) => _plugin = plugin;

        public Result<IReadOnlyList<LoadedPlugin>> Instantiate(CompiledPluginAssembly compiled)
            => Result.Success<IReadOnlyList<LoadedPlugin>>([_plugin]);
    }
}
