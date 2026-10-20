using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Host.Tests;

/// <summary>
///     The tool door of the out-of-process MCP host: the one capability the wire
///     carries, and the one whose <c>Result</c> has to mean something.
/// </summary>
/// <remarks>
///     <para>
///         <c>IPluginLoadHost</c>'s contract is that a door reports the OUTCOME of a
///         registration: <c>Success</c> means the contribution is registered and
///         observable, <c>Failure</c> means it was refused and says why. The six
///         doors this route cannot carry are pinned by the parity table in
///         <c>PluginHostJsonRpcTests</c> (#1768). This file pins the other half:
///         the door that DOES carry its capability, so that "Success" is a
///         promise the host keeps.
///     </para>
///     <para>
///         A <c>Success</c> that registers nothing is the defect #1758 is about,
///         and the architecture guard for it is
///         <c>tests/Harbor.Architecture.Tests/ExtensionAxisDoorHonestyRule.cs</c>.
///     </para>
/// </remarks>
public sealed class McpPluginLoadHostContractTests
{
    private static McpPluginLoadHost CreateHost() =>
        new(NullLoggerFactory.Instance, new NullEventBus());

    /// <summary>
    ///     The tool door succeeds, and the tool is actually there: <c>Success</c>
    ///     means the registration took effect and is observable.
    /// </summary>
    [Test]
    public async Task RegisterTool_Succeeds_AndTheToolIsObservable()
    {
        var host = CreateHost();

        Result result = host.RegisterTool(new ProbeTool());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(host.Tools.ContainsKey("probe")).IsTrue()
            .Because("Success means the registration took effect and is observable. The tool must be "
                   + "in the registry the MCP server serves tools/list from.");
    }

    /// <summary>
    ///     A duplicate tool name is a refusal too: the second registration did not
    ///     happen, so it must not claim it did.
    /// </summary>
    [Test]
    public async Task RegisterTool_RefusesADuplicate()
    {
        var host = CreateHost();
        host.RegisterTool(new ProbeTool());

        Result duplicate = host.RegisterTool(new ProbeTool());

        await Assert.That(duplicate.IsFailure).IsTrue();
        await Assert.That(duplicate.Error).Contains("already registered");
    }

    private sealed class ProbeTool : ITool
    {
        public ToolName Name => ToolName.Create("probe");
        public string DisplayName => "Probe";
        public string Description => "Probes the tool door";
        public JsonDocument ParameterSchema => JsonDocument.Parse("""{"type":"object"}""");
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("probe"));
    }
}
