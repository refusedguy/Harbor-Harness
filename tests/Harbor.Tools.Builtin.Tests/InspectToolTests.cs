using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Agents;
using Harbor.Registries.Providers;
using Harbor.Registries.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="InspectTool" /> — the read-only runtime catalog
///     (#1732). The catalog's whole claim is that it describes the runtime it
///     runs in, so the tests give it the REAL registries the product ships
///     rather than doubles: a catalog that disagreed with the registry it
///     reads is the defect, and a double would not catch it.
/// </summary>
public class InspectToolTests
{
    [Test]
    public async Task Name_IsHarborInspect()
    {
        var tool = NewTool();

        await Assert.That(tool.Name.Value).IsEqualTo("harbor_inspect");
    }

    [Test]
    public async Task SafetyProfile_IsOpaque_Because_TheCatalog_Takes_No_Path_And_No_Command()
    {
        var tool = NewTool();

        await Assert.That(tool.SafetyProfile).IsEqualTo(ToolSafetyProfile.Opaque);
    }

    [Test]
    [Arguments("""{"section":"nope"}""")]
    [Arguments("""{"name":""}""")]
    public async Task ValidateArguments_RejectsAnUnknownSection_And_AnEmptyName(string json)
    {
        var tool = NewTool();

        var result = tool.ValidateArguments(Args(json));

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ValidateArguments_AcceptsAnEmptyCall()
    {
        var tool = NewTool();

        var result = tool.ValidateArguments(Args("{}"));

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Overview_NamesEverySection_And_HowTo_Ask_For_One()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("{}"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("sections: overview, tools, agents, providers, skills, plugins, versions, limits");
        await Assert.That(result.Output).Contains("harbor 0.4.0");
        await Assert.That(result.Output).Contains("agent code");
    }

    [Test]
    public async Task Tools_ListsEveryRegisteredTool_With_The_Verdict_The_Agent_Would_Get()
    {
        var tools = new ToolRegistry();
        tools.Register(new FakeTool("read", "Read", "Reads a file.", ToolSafetyProfile.Path()));
        tools.Register(new FakeTool("write", "Write", "Writes a file.", ToolSafetyProfile.Path()));
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("some-model", "some-provider"));

        var tool = NewTool(tools, agents);

        var result = await tool.ExecuteAsync(Args("""{"section":"tools"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("tools: 2 registered, 1 callable by agent 'code'");
        await Assert.That(result.Output).Contains("read | Read | Reads a file. | parallel | allow");
        await Assert.That(result.Output).Contains("write | Write | Writes a file. | parallel | ask");
    }

    [Test]
    public async Task Tools_Shows_One_Entry_In_Full()
    {
        var tools = new ToolRegistry();
        tools.Register(new FakeTool("read", "Read", "Reads a file.", ToolSafetyProfile.Path()));
        tools.Freeze();

        var tool = NewTool(tools);

        var result = await tool.ExecuteAsync(Args("""{"section":"tools","name":"read"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("tool read");
        await Assert.That(result.Output).Contains("description: Reads a file.");
        await Assert.That(result.Output).Contains("argument guard: path");
        await Assert.That(result.Output).Contains("\"path\"");
    }

    [Test]
    public async Task Tools_An_Unknown_Name_Says_What_Exists()
    {
        var tools = new ToolRegistry();
        tools.Register(new FakeTool("read", "Read", "Reads a file.", ToolSafetyProfile.Path()));
        tools.Freeze();

        var tool = NewTool(tools);

        var result = await tool.ExecuteAsync(Args("""{"section":"tools","name":"reed"}"""), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("No tool named 'reed'");
        await Assert.That(result.Output).Contains("read");
    }

    [Test]
    public async Task Tools_An_Unknown_Section_Says_What_Exists()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("""{"section":"wizards"}"""), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("Unknown section 'wizards'");
        await Assert.That(result.Output).Contains("overview");
    }

    [Test]
    public async Task Agents_Lists_The_Registered_Agents()
    {
        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("some-model", "some-provider"));

        var tool = NewTool(agents: agents);

        var result = await tool.ExecuteAsync(Args("""{"section":"agents"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("agents: 1 registered");
        await Assert.That(result.Output).Contains("code | Code |");
        await Assert.That(result.Output).Contains("model some-provider/some-model");
    }

    [Test]
    public async Task Providers_Lists_The_Catalogue_And_Never_The_Credential()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("""{"section":"providers"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("kilocode | Kilocode | default model tencent/hy3:free | api key required");
        // The catalogue entry carries no credential field at all, so the
        // environment variable a provider reads cannot travel with the answer.
        await Assert.That(result.Output).DoesNotContain("KILO_API_KEY");
    }

    [Test]
    public async Task Plugins_Lists_The_Axes_With_Their_Verdicts_And_Reasons()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("""{"section":"plugins"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("IToolPlugin -> RegisterTool | carried |");
        await Assert.That(result.Output).Contains("IProviderPlugin -> RegisterProvider | refused |");
        await Assert.That(result.Output).Contains("an ILlmClient is a live streaming client");
    }

    [Test]
    public async Task Plugins_Shows_One_Axis_With_Its_Full_Reason()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("""{"section":"plugins","name":"IToolPlugin"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("axis IToolPlugin -> RegisterTool");
        await Assert.That(result.Output).Contains("route: mcp-stdio");
        await Assert.That(result.Output).Contains("carriage: carried");
        await Assert.That(result.Output).Contains("reason: the tool's name travels on tools/list");
    }

    [Test]
    public async Task Versions_Reports_Both_Versions()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("""{"section":"versions"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("harbor: 0.4.0");
        await Assert.That(result.Output).Contains("plugin manifest schema: 1");
    }

    [Test]
    public async Task Limits_Reports_The_Step_Budget_And_The_Permission_Counts()
    {
        var tools = new ToolRegistry();
        tools.Register(new FakeTool("read", "Read", "Reads a file.", ToolSafetyProfile.Path()));
        tools.Register(new FakeTool("write", "Write", "Writes a file.", ToolSafetyProfile.Path()));
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("some-model", "some-provider"));

        var tool = NewTool(tools, agents);

        var result = await tool.ExecuteAsync(Args("""{"section":"limits"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("limits for agent 'code'");
        await Assert.That(result.Output).Contains("max steps: 50");
        await Assert.That(result.Output).Contains("tool permissions: 1 allow, 1 ask, 0 deny (of 2)");
        await Assert.That(result.Output).Contains("argument guards: 2 path, 0 command, 0 unguarded");
    }

    [Test]
    public async Task Skills_Without_A_Registry_Says_So_Instead_Of_Pretending_To_Be_Empty()
    {
        var tool = NewTool();

        var result = await tool.ExecuteAsync(Args("""{"section":"skills"}"""), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("No skill registry");
    }

    [Test]
    public async Task Skills_Lists_What_The_Model_May_Load()
    {
        var skills = new FakeSkillRegistry(new SkillDescriptor("code-review", "Review a change.", "/tmp/skill.md"));
        var tool = NewTool(skills: skills);

        var result = await tool.ExecuteAsync(Args("""{"section":"skills"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("skills: 1 available to the model");
        await Assert.That(result.Output).Contains("code-review | Review a change. | model may invoke");
        // The path of the skill file is the user's own directory layout — the
        // catalog does not carry it.
        await Assert.That(result.Output).DoesNotContain("/tmp/skill.md");
    }

    [Test]
    public async Task Plugins_Lists_The_Published_Live_Composition()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-inspect-{Guid.NewGuid():N}", "inventory.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "route": "mcp-stdio",
              "plugins": [
                {
                  "name": "todo-write",
                  "version": "1.2.0",
                  "phase": "active",
                  "axes": ["IToolPlugin"],
                  "registrations": [
                    { "door": "RegisterTool", "subject": "todo_write", "outcome": "registered", "reason": null }
                  ],
                  "failureReason": null
                }
              ]
            }
            """);

        try
        {
            var tool = NewTool(pluginInventoryPath: path);

            var result = await tool.ExecuteAsync(Args("""{"section":"plugins"}"""), TestToolContext.Create());

            await Assert.That(result).HasSucceeded();
            await Assert.That(result.Output).Contains("plugins: 1 in the published composition");
            await Assert.That(result.Output).Contains("- todo-write | 1.2.0 | active | axes IToolPlugin");
            await Assert.That(result.Output).Contains("RegisterTool todo_write: registered");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public async Task Plugins_Say_So_When_No_Composition_Was_Published()
    {
        var tool = NewTool(pluginInventoryPath: Path.Combine(Path.GetTempPath(), $"harbor-inspect-missing-{Guid.NewGuid():N}.json"));

        var result = await tool.ExecuteAsync(Args("""{"section":"plugins"}"""), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("plugins: (no plugin composition has been published yet)");
    }

    [Test]
    public async Task The_Catalog_Changes_Nothing()
    {
        var tools = new ToolRegistry();
        tools.Register(new FakeTool("read", "Read", "Reads a file.", ToolSafetyProfile.Path()));
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("some-model", "some-provider"));

        var tool = NewTool(tools, agents);
        int before = tools.GetAllTools().Count;

        await tool.ExecuteAsync(Args("{}"), TestToolContext.Create());
        await tool.ExecuteAsync(Args("""{"section":"tools"}"""), TestToolContext.Create());
        await tool.ExecuteAsync(Args("""{"section":"limits"}"""), TestToolContext.Create());

        await Assert.That(tools.GetAllTools().Count).IsEqualTo(before);
        await Assert.That(agents.GetAllAgents().Count).IsEqualTo(1);
    }

    private static InspectTool NewTool(
        ToolRegistry? tools = null,
        AgentRegistry? agents = null,
        ProviderRegistry? providers = null,
        ISkillRegistry? skills = null,
        string? pluginInventoryPath = null)
        => new(
            tools ?? new ToolRegistry(),
            agents ?? new AgentRegistry(),
            providers ?? new ProviderRegistry(),
            skills,
            null,
            pluginInventoryPath,
            Facts(),
            NullLogger<InspectTool>.Instance);

    private static RuntimeApiFacts Facts() => new(
        "0.4.0",
        "1",
        [
            new RuntimeExtensionAxis(
                "IToolPlugin",
                "RegisterTool",
                "mcp-stdio",
                "carried",
                "the tool's name travels on tools/list"),
            new RuntimeExtensionAxis(
                "IProviderPlugin",
                "RegisterProvider",
                "mcp-stdio",
                "refused",
                "an ILlmClient is a live streaming client"),
        ],
        [
            new RuntimeProviderEntry(
                "kilocode",
                "Kilocode",
                "tencent/hy3:free",
                true,
                "Get a key at https://kilocode.ai/keys"),
        ]);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>A tool that does nothing — the catalog only reads its declaration.</summary>
    private sealed class FakeTool(
        string name,
        string displayName,
        string description,
        ToolSafetyProfile profile) : ITool
    {
        public ToolName Name => ToolName.Create(name);

        public string DisplayName => displayName;

        public string Description => description;

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"}}}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public ToolSafetyProfile SafetyProfile { get; } = profile;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("executed"));
    }

    /// <summary>A skill registry that hands back a fixed catalogue.</summary>
    private sealed class FakeSkillRegistry(params SkillDescriptor[] skills) : ISkillRegistry
    {
        public IReadOnlyList<SkillDescriptor> GetSkills(
            string workingDirectory,
            SkillCaller caller,
            string? mode = null,
            string? category = null)
            => skills;

        public Result<SkillDescriptor> ResolveForCall(
            string workingDirectory,
            string name,
            SkillCaller caller,
            string? mode = null,
            string? category = null)
        {
            foreach (SkillDescriptor skill in skills)
            {
                if (string.Equals(skill.Name, name, StringComparison.Ordinal))
                {
                    return Result.Success(skill);
                }
            }

            return Result.Failure<SkillDescriptor>($"No skill named '{name}'.");
        }
    }
}
