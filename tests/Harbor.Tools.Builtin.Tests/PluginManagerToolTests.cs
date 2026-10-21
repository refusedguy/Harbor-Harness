using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="PluginManagerTool" /> — the agent-facing plugin
///     management tool (#1738). The behaviour under test is the GATE: a change
///     is proposed, the user is asked, and nothing happens unless the user
///     says yes.
/// </summary>
public class PluginManagerToolTests
{
    private readonly FakePluginInventory _inventory = new();
    private readonly List<PermissionRequest> _asks = [];

    private PluginManagerTool NewTool() => new(_inventory, NullLogger<PluginManagerTool>.Instance);

    /// <summary>A context whose approval answers with <paramref name="answer" />.</summary>
    private ToolContext Context(PermissionAction answer)
        => TestToolContext.Create(ask: (request, _) =>
        {
            _asks.Add(request);
            return Task.FromResult(new PermissionResponse(answer, PersistDecision: false));
        });

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Name_IsPluginManager()
    {
        ITool tool = NewTool();
        await Assert.That(tool.Name.Value).IsEqualTo("plugin_manager");
    }

    [Test]
    public async Task ExecutionMode_IsSequential()
    {
        ITool tool = NewTool();
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
    }

    [Test]
    public async Task List_ReportsTheInstalledPlugins_AndAsksNobody()
    {
        _inventory.Entries.Add(new PluginInventoryEntry(
            "TodoWrite.cs",
            PluginScope.Global,
            "/home/u/.harbor/plugins/TodoWrite.cs",
            Enabled: true,
            DeclaredVersion: "1.2.0",
            DeclaredAxes: ["IToolPlugin"],
            RequiredHarborVersion: "0.4.0",
            Compatible: true));

        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(Args("""{"action":"list"}"""), Context(PermissionAction.Allow));

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("TodoWrite.cs");
        await Assert.That(result.Output).Contains("1.2.0");
        await Assert.That(_asks).IsEmpty()
            .Because("reading the inventory changes nothing, so it must not spend a question");
    }

    [Test]
    public async Task List_WithoutAnyPlugin_SaysSo()
    {
        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(Args("""{"action":"list"}"""), Context(PermissionAction.Allow));

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("No plugins are installed");
    }

    [Test]
    public async Task Install_AsksTheUser_AndNamesTheEffect()
    {
        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"install","source":"/tmp/TodoWrite.cs"}"""),
            Context(PermissionAction.Allow));

        await Assert.That(result).HasSucceeded();
        await Assert.That(_asks.Count).IsEqualTo(1);
        await Assert.That(_asks[0].Permission).IsEqualTo("plugin_manager");
        await Assert.That(_asks[0].Pattern).Contains("TodoWrite.cs");
        await Assert.That(_asks[0].Pattern).Contains("global");
        await Assert.That(_inventory.Applied.Count).IsEqualTo(1);
        await Assert.That(_inventory.Applied[0].Action).IsEqualTo(PluginInventoryAction.Install);
        await Assert.That(_inventory.Applied[0].SourcePath).IsEqualTo("/tmp/TodoWrite.cs");
    }

    [Test]
    public async Task Install_WithoutApproval_ChangesNothing()
    {
        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"install","source":"/tmp/TodoWrite.cs"}"""),
            Context(PermissionAction.Deny));

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("Next step");
        await Assert.That(_inventory.Applied).IsEmpty()
            .Because("the action is not performed without confirmation");
    }

    [Test]
    public async Task Uninstall_WithoutApproval_ChangesNothing()
    {
        _inventory.Entries.Add(new PluginInventoryEntry(
            "TodoWrite.cs",
            PluginScope.Global,
            "/home/u/.harbor/plugins/TodoWrite.cs",
            Enabled: true,
            DeclaredVersion: null,
            DeclaredAxes: [],
            RequiredHarborVersion: null,
            Compatible: true));

        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"uninstall","name":"TodoWrite.cs"}"""),
            Context(PermissionAction.Deny));

        await Assert.That(result).HasFailed();
        await Assert.That(_inventory.Applied).IsEmpty()
            .Because("the action is not performed without confirmation");
    }

    [Test]
    public async Task Uninstall_WithApproval_AppliesTheChange()
    {
        _inventory.Entries.Add(new PluginInventoryEntry(
            "TodoWrite.cs",
            PluginScope.Global,
            "/home/u/.harbor/plugins/TodoWrite.cs",
            Enabled: true,
            DeclaredVersion: null,
            DeclaredAxes: [],
            RequiredHarborVersion: null,
            Compatible: true));

        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"uninstall","name":"TodoWrite.cs"}"""),
            Context(PermissionAction.Allow));

        await Assert.That(result).HasSucceeded();
        await Assert.That(_inventory.Applied.Count).IsEqualTo(1);
        await Assert.That(_inventory.Applied[0].Action).IsEqualTo(PluginInventoryAction.Uninstall);
        await Assert.That(_inventory.Applied[0].Name).IsEqualTo("TodoWrite.cs");
    }

    [Test]
    public async Task Disable_WithApproval_AsksForTheDisable()
    {
        _inventory.Entries.Add(new PluginInventoryEntry(
            "TodoWrite.cs",
            PluginScope.Project,
            "/work/.harbor/plugins/TodoWrite.cs",
            Enabled: true,
            DeclaredVersion: null,
            DeclaredAxes: [],
            RequiredHarborVersion: null,
            Compatible: true));

        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"disable","name":"TodoWrite.cs"}"""),
            Context(PermissionAction.Allow));

        await Assert.That(result).HasSucceeded();
        await Assert.That(_asks[0].Pattern).Contains("Disable");
        await Assert.That(_asks[0].Pattern).Contains("project");
        await Assert.That(_inventory.Applied[0].Action).IsEqualTo(PluginInventoryAction.Disable);
    }

    [Test]
    public async Task Approval_CoversThatCallOnly_TheNextCallAsksAgain()
    {
        // The "always" answer is what the plain renderer offers. The tool must
        // treat it as "yes, this time": an approval is never written back to a
        // ruleset, so the session's permission mode is unchanged and the NEXT
        // call asks again.
        _inventory.Entries.Add(new PluginInventoryEntry(
            "TodoWrite.cs",
            PluginScope.Global,
            "/home/u/.harbor/plugins/TodoWrite.cs",
            Enabled: true,
            DeclaredVersion: null,
            DeclaredAxes: [],
            RequiredHarborVersion: null,
            Compatible: true));

        var asks = 0;
        ToolContext context = TestToolContext.Create(ask: (_, _) =>
        {
            asks++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Allow, PersistDecision: true));
        });

        ITool tool = NewTool();
        await tool.ExecuteAsync(Args("""{"action":"uninstall","name":"TodoWrite.cs"}"""), context);
        await tool.ExecuteAsync(Args("""{"action":"uninstall","name":"TodoWrite.cs"}"""), context);

        await Assert.That(asks).IsEqualTo(2)
            .Because("an approval covers that call only — it is never written back to a ruleset, so the "
                     + "next call asks again");
    }

    [Test]
    public async Task UnknownPlugin_IsRefused_BeforeTheUserIsAsked()
    {
        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"uninstall","name":"Missing.cs"}"""),
            Context(PermissionAction.Allow));

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("No plugin named 'Missing.cs'");
        await Assert.That(result.Output).Contains("Next step");
        await Assert.That(_asks).IsEmpty();
    }

    [Test]
    public async Task ApplyFailure_IsReportedWithItsReason()
    {
        _inventory.Entries.Add(new PluginInventoryEntry(
            "TodoWrite.cs",
            PluginScope.Global,
            "/home/u/.harbor/plugins/TodoWrite.cs",
            Enabled: true,
            DeclaredVersion: null,
            DeclaredAxes: [],
            RequiredHarborVersion: null,
            Compatible: true));
        _inventory.ApplyError = "the scope is read-only";

        ITool tool = NewTool();
        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"uninstall","name":"TodoWrite.cs"}"""),
            Context(PermissionAction.Allow));

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("the scope is read-only");
    }

    [Test]
    [Arguments("""{"action":"list"}""", true)]
    [Arguments("""{"action":"install"}""", false)]
    [Arguments("""{"action":"install","source":"/tmp/a.cs","scope":"nowhere"}""", false)]
    [Arguments("""{"action":"uninstall"}""", false)]
    [Arguments("""{"action":"enable"}""", false)]
    [Arguments("""{"action":"nonsense"}""", false)]
    [Arguments("""{}""", false)]
    public async Task ValidateArguments_Theory(string json, bool expectSuccess)
    {
        ITool tool = NewTool();
        Result validated = tool.ValidateArguments(Args(json));
        await Assert.That(validated.IsSuccess).IsEqualTo(expectSuccess);
    }
}
