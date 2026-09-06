using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

public class AgentRegistryTests
{
    [Test]
    public async Task GetAgent_RegisteredAgent_ReturnsSuccess()
    {
        var registry = new AgentRegistry();
        var agent = AgentDefinition.CodeDefault("gpt-4o", "openai");
        registry.Register(agent);

        var result = registry.GetAgent(agent.Name);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsEqualTo(agent.Name);
    }

    [Test]
    public async Task GetAgent_UnregisteredAgent_ReturnsFailure()
    {
        var registry = new AgentRegistry();
        var result = registry.GetAgent(AgentName.Create("nonexistent"));
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Register_DuplicateAgent_ReturnsFailure()
    {
        var registry = new AgentRegistry();
        var agent = AgentDefinition.CodeDefault("gpt-4o", "openai");
        registry.Register(agent);
        var result = registry.Register(agent);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Unregister_ExistingAgent_ReturnsSuccess()
    {
        var registry = new AgentRegistry();
        var agent = AgentDefinition.CodeDefault("gpt-4o", "openai");
        registry.Register(agent);
        var result = registry.Unregister(agent.Name);
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unregister_NonexistentAgent_ReturnsFailure()
    {
        var registry = new AgentRegistry();
        var result = registry.Unregister(AgentName.Create("nonexistent"));
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task GetAllAgents_EmptyRegistry_ReturnsEmpty()
    {
        var registry = new AgentRegistry();
        await Assert.That(registry.GetAllAgents().Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAllAgents_WithAgents_ReturnsAll()
    {
        var registry = new AgentRegistry();
        registry.Register(AgentDefinition.CodeDefault("gpt-4o", "openai"));
        registry.Register(AgentDefinition.PlanDefault("gpt-4o", "openai"));
        registry.Register(AgentDefinition.ExploreDefault("gpt-4o", "openai"));
        await Assert.That(registry.GetAllAgents().Count).IsEqualTo(3);
    }
}

public class PermissionRulesetTests
{
    [Arguments("read", "src/main.cs", PermissionAction.Allow)]
    [Arguments("bash", "cat README.md", PermissionAction.Allow)]
    [Arguments("edit", "src/main.cs", PermissionAction.Allow)]
    [Arguments("edit", "*.env", PermissionAction.Deny)]
    [Arguments("bash", "rm -rf /", PermissionAction.Deny)]
    [Arguments("bash", "sudo ls", PermissionAction.Deny)]
    [Arguments("write", "src/main.cs", PermissionAction.Allow)]
    [Arguments("write", "/etc/passwd", PermissionAction.Ask)]
    [Test]
    public async Task PermissionRuleset_Default_Evaluate_ReturnsExpectedAction(
        string tool,
        string arg,
        PermissionAction expected)
    {
        await Assert.That(PermissionRuleset.Default.Evaluate(tool, arg)).IsEqualTo(expected);
    }

    [Test]
    public async Task PermissionRuleset_Empty_Evaluate_ReturnsAsk()
    {
        await Assert.That(PermissionRuleset.Empty.Evaluate("read", "any/path")).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task PermissionRuleset_CustomAllowAll_Evaluate_ReturnsAllow()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("*", "*", PermissionAction.Allow)
        });
        await Assert.That(ruleset.Evaluate("bash", "rm -rf /")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task PermissionRuleset_DenyBash_Evaluate_ReturnsDeny()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("bash", "*", PermissionAction.Deny)
        });
        await Assert.That(ruleset.Evaluate("bash", "ls")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task PermissionRuleset_PathSpecificRule_Evaluate_ReturnsCorrectAction()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("read", "src/*", PermissionAction.Allow),
            new PermissionRule("read", "*", PermissionAction.Deny)
        });
        await Assert.That(ruleset.Evaluate("read", "src/main.cs")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(ruleset.Evaluate("read", "secret.txt")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task PermissionRuleset_Merge_CombinesRulesets()
    {
        var baseRuleset = new PermissionRuleset(new[]
        {
            new PermissionRule("read", "*", PermissionAction.Allow)
        });
        var overrideRuleset = new PermissionRuleset(new[]
        {
            new PermissionRule("read", "*.secret", PermissionAction.Deny)
        });
        var merged = baseRuleset.Merge(overrideRuleset);
        await Assert.That(merged.Evaluate("read", "*.secret")).IsEqualTo(PermissionAction.Deny);
        await Assert.That(merged.Evaluate("read", "normal.txt")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task PermissionRuleset_Default_ReadAllowed()
    {
        await Assert.That(PermissionRuleset.Default.Evaluate("read", "src/main.cs")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task PermissionRuleset_Default_TreeAllowed()
    {
        await Assert.That(PermissionRuleset.Default.Evaluate("tree", "src")).IsEqualTo(PermissionAction.Allow);
    }
}

public class ToolRegistryTests
{
    private sealed class StubTool : ITool
    {
        public ToolName Name => ToolName.Create("stub-tool");
        public string DisplayName => "Stub Tool";
        public string Description => "A stub tool for testing.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema => JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("stub"));
    }

    [Test]
    public async Task ToolRegistry_Register_ReturnsSuccess()
    {
        var registry = new ToolRegistry();
        var result = registry.Register(new StubTool());
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ToolRegistry_Register_Duplicate_ReturnsFailure()
    {
        var registry = new ToolRegistry();
        var tool = new StubTool();
        registry.Register(tool);
        var result = registry.Register(tool);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ToolRegistry_GetTool_ReturnsRegisteredTool()
    {
        var registry = new ToolRegistry();
        var tool = new StubTool();
        registry.Register(tool);
        var result = registry.GetTool(tool.Name);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsEqualTo(tool.Name);
    }

    [Test]
    public async Task ToolRegistry_GetTool_Unregistered_ReturnsFailure()
    {
        var registry = new ToolRegistry();
        var result = registry.GetTool(ToolName.Create("nonexistent"));
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ToolRegistry_ResolveTools_ReturnsRegisteredTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool());
        registry.Freeze();
        var result = registry.ResolveTools("code");
        await Assert.That(result.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ToolRegistry_ResolveTools_FilteredByPermission_ReturnsOnlyAllowed()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool());
        registry.Freeze();

        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("stub-tool", "*", PermissionAction.Deny)
        });

        var result = registry.ResolveTools("code", permission);
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
