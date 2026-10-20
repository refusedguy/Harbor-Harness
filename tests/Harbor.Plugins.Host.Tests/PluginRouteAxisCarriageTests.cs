using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Host;
using Harbor.Plugins.Registration;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Host.Tests;

/// <summary>
///     What the out-of-process route does with each axis of extension, as an
///     executable spec of the declaration in
///     <c>PluginRouteAxisRegistry</c> (issue #1723).
/// </summary>
/// <remarks>
///     <para>
///         The parity table in <c>PluginHostJsonRpcTests</c> pins the VERDICT per
///         axis — tools are served, the rest are refused. This file pins the
///         other half: that the verdict is the one the product DECLARES, that
///         the refusal carries the declared reason (the sentence a plugin
///         author reads), and that the carried axis really crosses the process
///         boundary — plugin instance, registrar, door, wire, call.
///     </para>
///     <para>
///         The structural half — that every axis has a declared fate at all, and
///         that the implementation matches it — is graded by
///         <c>tests/Harbor.Architecture.Tests/ExtensionAxisRouteCarriageRule.cs</c>.
///     This file is the behavioural half, and it lives with the host because
///         the host is what has to obey the declaration.
///     </para>
/// </remarks>
public sealed class PluginRouteAxisCarriageTests
{
    /// <summary>The name of the tool the crossing plugin contributes.</summary>
    private const string CrossingToolName = "crossing_probe";

    private static McpPluginLoadHost CreateHost() =>
        new(NullLoggerFactory.Instance, new NullEventBus());

    /// <summary>
    ///     Every axis behaves as the declaration says: a carried axis registers,
    ///     a refused axis answers <c>Result.Failure</c> with the declared reason.
    /// </summary>
    [Test]
    public async Task EveryDeclaredAxis_BehavesAsTheDeclarationSays()
    {
        var host = CreateHost();

        foreach (PluginRouteAxis row in PluginRouteAxisRegistry.ForRoute(PluginRouteAxisRegistry.McpStdioRouteId))
        {
            Result result = CallDoor(host, row.Door);

            if (row.Carriage is PluginAxisCarriage.Carried)
            {
                await Assert.That(result.IsSuccess).IsTrue()
                    .Because(
                        $"the {row.Door} axis is CARRIED on the {row.Route} route: the door has to accept the "
                        + "contribution the declaration promises to deliver");

                await Assert.That(host.Tools.ContainsKey(CrossingToolName)).IsTrue()
                    .Because(
                        "a carried axis registers for real: the contribution is observable in the registry the "
                        + "MCP server serves tools/list from");
            }
            else
            {
                await Assert.That(result.IsSuccess).IsFalse()
                    .Because(
                        $"the {row.Door} axis is {row.Carriage} on the {row.Route} route: the door has to "
                        + "refuse, never answer a success it did not perform (#1758)");

                await Assert.That(result.Error).Contains(row.Reason)
                    .Because(
                        "the refusal has to carry the reason the product declares — a refusal without a reason "
                        + "is the defect #1723 is about, in the shape a plugin author sees it");

                await Assert.That(result.Error).Contains(PluginRouteAxisRegistry.MarkerOf(row.Door))
                    .Because("the refusal has to name the axis it is about, so the plugin author knows which "
                             + "contribution was dropped and why");
            }
        }
    }

    /// <summary>
    ///     The carried axis crosses the process boundary in the form the
    ///     in-process route gives it: a plugin instance, dispatched by the
    ///     registrar, accepted by the door, advertised on <c>tools/list</c> and
    ///     executed by <c>tools/call</c>.
    /// </summary>
    [Test]
    public async Task TheCarriedAxis_CrossesTheProcessBoundary()
    {
        var host = CreateHost();
        var registrar = new PluginRegistrar(
            Path.Combine(Path.GetTempPath(), "harbor-plugin-route-carriage"),
            NullLogger<PluginRegistrar>.Instance,
            NullLoggerFactory.Instance);

        var plugin = new CrossingToolPlugin();
        var loaded = new LoadedPlugin(
            plugin,
            plugin.Name,
            plugin.Version,
            typeof(CrossingToolPlugin),
            Path.Combine(Path.GetTempPath(), "harbor-plugin-route-carriage", "crossing.cs"),
            "sha256-crossing-probe",
            LoadedFromCache: false);

        Result registration = registrar.Register(loaded, host);

        await Assert.That(registration.IsSuccess).IsTrue()
            .Because(
                "the tool axis is CARRIED: the registrar dispatches the marker, the door accepts the "
                + "contribution, and nothing is refused — a failure here would mean the route drops the one "
                + "axis it declares it carries");

        var server = new McpStdioServer(host, NullLogger<McpStdioServer>.Instance);

        string? listFrame = await server
            .HandleLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None)
            .ConfigureAwait(false);
        await Assert.That(listFrame).IsNotNull();

        using var list = JsonDocument.Parse(listFrame!);
        var tools = list.RootElement.GetProperty("result").GetProperty("tools");
        await Assert.That(tools.GetArrayLength()).IsEqualTo(1)
            .Because("the plugin contributed exactly one tool, and the wire has to advertise it");
        await Assert.That(tools[0].GetProperty("name").GetString()).IsEqualTo(CrossingToolName);
        await Assert.That(tools[0].GetProperty("description").GetString()).Contains("Crosses");

        string? callFrame = await server
            .HandleLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"crossing_probe","arguments":{"text":"hi"}}}""",
                CancellationToken.None)
            .ConfigureAwait(false);
        await Assert.That(callFrame).IsNotNull();

        using var call = JsonDocument.Parse(callFrame!);
        var result = call.RootElement.GetProperty("result");
        await Assert.That(result.GetProperty("isError").GetBoolean()).IsEqualTo(false);
        await Assert.That(result.GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo("crossing:hi")
            .Because(
                "the tool's result has to come back over the wire — the carried axis is not a name on a "
                + "list, it is a callable");
    }

    /// <summary>
    ///     The declaration names every door the route refuses, and the refusal
    ///     names the axis — the two together are what a plugin author reads.
    /// </summary>
    [Test]
    public async Task EveryRefusedDoor_AnswersWithADeclaredReason()
    {
        var host = CreateHost();
        var refused = PluginRouteAxisRegistry.ForRoute(PluginRouteAxisRegistry.McpStdioRouteId)
            .Where(r => r.Carriage is not PluginAxisCarriage.Carried)
            .ToList();

        await Assert.That(refused.Count).IsGreaterThan(0)
            .Because("the out-of-process route refuses the axes it cannot carry; a route that refused nothing "
                   + "would make this suite vacuous");

        foreach (PluginRouteAxis row in refused)
        {
            Result result = CallDoor(host, row.Door);

            await Assert.That(result.IsFailure).IsTrue()
                .Because($"the {row.Door} axis is {row.Carriage} on this route, so the door has to refuse");
            await Assert.That(result.Error).Contains(row.Reason)
                .Because($"the {row.Door} refusal has to carry its declared reason");
        }
    }

    /// <summary>
    ///     Calls one door of the route with a probe contribution, so the verdict
    ///     per axis can be checked against the declaration row by row.
    /// </summary>
    private static Result CallDoor(McpPluginLoadHost host, string door)
    {
        var providerId = ProviderId.TryCreate("carriage-probe");
        if (providerId.IsFailure)
            throw new InvalidOperationException("the probe provider id is not valid");

        return door switch
        {
            nameof(IPluginLoadHost.RegisterTool) => host.RegisterTool(new CrossingTool()),
            nameof(IPluginLoadHost.RegisterProvider) => host.RegisterProvider(providerId.Value, () => null!),
            nameof(IPluginLoadHost.RegisterAgent) =>
                host.RegisterAgent(AgentDefinition.CodeDefault("probe-model", "probe-provider")),
            nameof(IPluginLoadHost.RegisterTuiPlugin) => host.RegisterTuiPlugin(new ProbeTuiPlugin()),
            nameof(IPluginLoadHost.RegisterPanelProvider) => host.RegisterPanelProvider(new ProbePanel()),
            nameof(IPluginLoadHost.RegisterSessionStore) => host.RegisterSessionStore("carriage-probe", () => null!),
            nameof(IPluginLoadHost.RegisterTuiBackend) => host.RegisterTuiBackend("carriage-probe", null, () => null!),
            _ => throw new InvalidOperationException($"no probe contribution for the door '{door}'"),
        };
    }

    /// <summary>A plugin that contributes one tool — the carried axis, in the shape a plugin author writes it.</summary>
    private sealed class CrossingToolPlugin : IToolPlugin
    {
        public string Name => "crossing-probe";

        public Version Version => new(1, 0, 0);

        public Version RequiredHarborVersion => new(0, 1, 0);

        public string Description => "Contributes one tool that crosses the process boundary";

        public void Initialize(PluginContext context)
        {
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool(new CrossingTool());
    }

    /// <summary>The tool the crossing plugin contributes.</summary>
    private sealed class CrossingTool : ITool
    {
        public ToolName Name => ToolName.Create(CrossingToolName);

        public string DisplayName => "Crossing probe";

        public string Description => "Crosses the process boundary and echoes its argument";

        public JsonDocument ParameterSchema => JsonDocument.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}}}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            string text = args.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString()!
                : string.Empty;
            return Task.FromResult(ToolResult.Success("crossing:" + text));
        }
    }

    /// <summary>A probe for the closed TUI seam (#564).</summary>
    private sealed class ProbeTuiPlugin : ITuiPlugin
    {
        public string Name => "carriage-probe";

        public Version Version => new(1, 0, 0);

        public string Description => "Carriage probe: the closed seam stays closed out-of-process";

        public void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels)
        {
        }
    }

    /// <summary>A probe for the panel axis.</summary>
    private sealed class ProbePanel : IPanelProvider
    {
        public string Id => "carriage-probe";

        public string Title => "Carriage probe";

        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

        public int DefaultSize => 5;

        public object? Build(PanelContext ctx) => null;

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }
}
