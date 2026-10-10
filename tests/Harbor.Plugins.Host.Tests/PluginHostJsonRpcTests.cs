using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Host;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Host.Tests;

/// <summary>
///     JSON-RPC surface tests for the out-of-process plugin host (#1055 slice
///     3 — the default CS-plugin route). Until this suite the host had no
///     assertion of its own: <c>Harbor.slnx</c> proved it compiles, nothing
///     proved <c>initialize</c> / <c>tools/list</c> / <c>tools/call</c> /
///     <c>ping</c>, the protocol version, or the NDJSON framing.
/// </summary>
/// <remarks>
///     <para>
///         The parity rows pin the capability table from #1041: tools are the
///         only capability the split host exposes over MCP; every other door
///         accepts (so plugin <c>Initialize</c> never throws) and discards.
///     </para>
///     <para>
///         The #1012 link set (which assemblies the host compiler resolves by
///         deployment-directory name, including the panel assembly) is pinned
///         where it lives — <c>Harbor.Plugins.Runtime.Tests/Compilation/PluginReferenceContractTests</c>
///         drives the same <c>PluginAssemblyReferences</c> the host process
///         builds — so this suite does not duplicate it.
///     </para>
///     <para>
///         The latency test is the issue's acceptance data: per-call dispatch
///         overhead of the JSON-RPC loop, measured loopback (no transport).
///     </para>
/// </remarks>
public class PluginHostJsonRpcTests
{
    private static McpPluginLoadHost CreateLoadHost() =>
        new(NullLoggerFactory.Instance, new NullEventBus());

    private static McpStdioServer CreateServer(McpPluginLoadHost loadHost) =>
        new(loadHost, NullLogger<McpStdioServer>.Instance);

    private static async Task<JsonDocument> CallAsync(McpStdioServer server, string request)
    {
        string? response = await server.HandleLineAsync(request, CancellationToken.None).ConfigureAwait(false);
        await Assert.That(response is not null).IsTrue()
            .Because("every request in this suite is addressed — a missing frame is a bug, not a protocol answer");
        return JsonDocument.Parse(response!);
    }

    [Test]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var server = CreateServer(CreateLoadHost());
        using var doc = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");

        var result = doc.RootElement.GetProperty("result");
        await Assert.That(result.GetProperty("protocolVersion").GetString()).IsEqualTo("2024-11-05");
        await Assert.That(result.GetProperty("serverInfo").GetProperty("name").GetString())
            .IsEqualTo("harbor-csharp-plugins");
    }

    [Test]
    public async Task ToolsList_EmptyWhenNoToolsRegistered()
    {
        var server = CreateServer(CreateLoadHost());
        using var doc = await CallAsync(server, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        await Assert.That(doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength())
            .IsEqualTo(0);
    }

    [Test]
    public async Task ToolsList_AdvertisesRegisteredTool()
    {
        var loadHost = CreateLoadHost();
        var registered = loadHost.RegisterTool(new EchoTool());
        await Assert.That(registered.IsSuccess).IsTrue();
        var duplicate = loadHost.RegisterTool(new EchoTool());
        await Assert.That(duplicate.IsSuccess).IsEqualTo(false);

        var server = CreateServer(loadHost);
        using var doc = await CallAsync(server, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        await Assert.That(tools.GetArrayLength()).IsEqualTo(1);
        var tool = tools[0];
        await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo("echo");
        await Assert.That(tool.GetProperty("description").GetString()).Contains("Echoes");
        await Assert.That(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("text", out _))
            .IsEqualTo(true);
    }

    [Test]
    public async Task ToolsCall_ExecutesToolAndReturnsTextResult()
    {
        var loadHost = CreateLoadHost();
        loadHost.RegisterTool(new EchoTool());
        var server = CreateServer(loadHost);
        using var doc = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");

        var result = doc.RootElement.GetProperty("result");
        await Assert.That(result.GetProperty("isError").GetBoolean()).IsEqualTo(false);
        await Assert.That(result.GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo("echo:hi");
    }

    [Test]
    public async Task ToolsCall_UnknownTool_ReturnsInvalidParams()
    {
        var server = CreateServer(CreateLoadHost());
        using var doc = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"nope"}}""");

        await Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32())
            .IsEqualTo(-32602);
    }

    [Test]
    public async Task ToolsCall_MissingName_ReturnsInvalidParams()
    {
        var server = CreateServer(CreateLoadHost());
        using var doc = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{}}""");

        await Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32())
            .IsEqualTo(-32602);
    }

    [Test]
    public async Task ToolsCall_ThrowingTool_ReturnsIsErrorAndServerStaysAlive()
    {
        var loadHost = CreateLoadHost();
        loadHost.RegisterTool(new ThrowingTool());
        var server = CreateServer(loadHost);
        using var failed = await CallAsync(
            server,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"boom"}}""");

        var result = failed.RootElement.GetProperty("result");
        await Assert.That(result.GetProperty("isError").GetBoolean()).IsEqualTo(true);
        await Assert.That(result.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("boom failed");

        // One bad tool cannot wedge the loop: the next frame is still served.
        using var ping = await CallAsync(server, """{"jsonrpc":"2.0","id":4,"method":"ping"}""");
        await Assert.That(ping.RootElement.TryGetProperty("result", out _)).IsEqualTo(true);
    }

    [Test]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var server = CreateServer(CreateLoadHost());
        using var doc = await CallAsync(server, """{"jsonrpc":"2.0","id":5,"method":"frobnicate"}""");

        await Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32())
            .IsEqualTo(-32601);
    }

    [Test]
    public async Task Ping_ReturnsEmptyResult()
    {
        var server = CreateServer(CreateLoadHost());
        using var doc = await CallAsync(server, """{"jsonrpc":"2.0","id":4,"method":"ping"}""");

        await Assert.That(doc.RootElement.GetProperty("result").ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task NotificationsAndUnaddressedFrames_GetNoResponse()
    {
        var server = CreateServer(CreateLoadHost());

        string? initialized = await server
            .HandleLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""", CancellationToken.None)
            .ConfigureAwait(false);
        await Assert.That(initialized is null).IsTrue()
            .Because("notifications/initialized is a no-op by contract");

        string? noMethod = await server
            .HandleLineAsync("""{"jsonrpc":"2.0","id":6}""", CancellationToken.None)
            .ConfigureAwait(false);
        await Assert.That(noMethod is null).IsTrue()
            .Because("a frame without a method is ignored, never answered");
    }

    /// <summary>
    ///     The #1041 capability table as an executable spec: tools are the one
    ///     capability the split host exposes; every other door accepts (so
    ///     plugin <c>Initialize</c> never throws) and discards — nothing but
    ///     tools reaches <c>tools/list</c>.
    /// </summary>
    [Test]
    public async Task NonToolRegistrations_AcceptedButNeverExposedOverMcp()
    {
        var loadHost = CreateLoadHost();

        var providerId = ProviderId.TryCreate("s3-probe");
        await Assert.That(providerId.IsSuccess).IsTrue();
        await Assert.That(loadHost.RegisterProvider(providerId.Value, () => null!).IsSuccess).IsTrue();
        await Assert.That(loadHost.RegisterAgent(AgentDefinition.CodeDefault("probe-model", "probe-provider")).IsSuccess)
            .IsTrue();
        await Assert.That(loadHost.RegisterTuiPlugin(new ProbeTuiPlugin()).IsSuccess).IsTrue();
        await Assert.That(loadHost.RegisterPanelProvider(new ProbePanel()).IsSuccess).IsTrue();
        await Assert.That(loadHost.RegisterSessionStore("s3store", () => null!).IsSuccess).IsTrue();
        await Assert.That(loadHost.RegisterTuiBackend("s3backend", null, () => null!).IsSuccess).IsTrue();

        var server = CreateServer(loadHost);
        using var doc = await CallAsync(server, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await Assert.That(doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength())
            .IsEqualTo(0);
    }

    /// <summary>
    ///     #1055 acceptance data: per-call dispatch overhead of the JSON-RPC
    ///     loop, measured loopback (no transport, no spawn). A tool call that
    ///     costs more than this bound in pure dispatch would be felt on every
    ///     agent turn, which is what this suite forbids.
    /// </summary>
    [Test]
    public async Task ToolsCall_DispatchLatency_StaysInteractive()
    {
        var loadHost = CreateLoadHost();
        loadHost.RegisterTool(new EchoTool());
        var server = CreateServer(loadHost);
        const string request =
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""";

        // Warmup: JIT + first-dispatch path, not measured.
        _ = await server.HandleLineAsync(request, CancellationToken.None).ConfigureAwait(false);

        const int iterations = 60;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _ = await server.HandleLineAsync(request, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();

        double meanMs = sw.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine(
            $"plugin-host tools/call dispatch: mean {meanMs:F3} ms over {iterations} calls "
            + "(in-process loopback, transport excluded)");

        await Assert.That(meanMs < 100.0).IsTrue()
            .Because($"loopback dispatch must stay far below interactivity budgets (measured mean {meanMs:F3} ms)");
    }

    private sealed class EchoTool : ITool
    {
        public ToolName Name => ToolName.Create("echo");
        public string DisplayName => "Echo";
        public string Description => "Echoes the 'text' argument back";
        public JsonDocument ParameterSchema => JsonDocument.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}}}""");
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
        {
            string text = args.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString()!
                : string.Empty;
            return Task.FromResult(ToolResult.Success("echo:" + text));
        }
    }

    private sealed class ThrowingTool : ITool
    {
        public ToolName Name => ToolName.Create("boom");
        public string DisplayName => "Boom";
        public string Description => "Always throws";
        public JsonDocument ParameterSchema => JsonDocument.Parse("""{"type":"object"}""");
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom failed");
    }

    private sealed class ProbeTuiPlugin : ITuiPlugin
    {
        public string Name => "s3probe";
        public Version Version => new(1, 0, 0);
        public string Description => "Parity probe: closed seam stays discarded out-of-process";

        public void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels)
        {
        }
    }

    private sealed class ProbePanel : IPanelProvider
    {
        public string Id => "s3probe";
        public string Title => "S3 Probe";
        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;
        public int DefaultSize => 5;
        public object? Build(PanelContext ctx) => null;
        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }
}
