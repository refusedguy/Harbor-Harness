using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Sessions;
namespace Harbor.Core.Tests;
/// <summary>
///     Tests for <see cref="SystemPromptBuilder" /> — verifies that the assembled prompt
///     contains the expected sections: environment metadata, agent-specific instructions,
///     and the available-tools listing (with snippet + guidelines).
/// </summary>
public class SystemPromptBuilderTests
{
    private static readonly ModelInfo TestModel = new(
        "test-model",
        "test",
        "Test Model",
        200_000,
        4_096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static AgentDefinition Agent(string? append = null) => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent.",
        "test-model",
        "test",
        PermissionRuleset.Default,
        SystemPromptAppend: append);

    private static SystemPromptContext Context(
        AgentDefinition agent,
        IReadOnlyList<ToolDescriptor> tools,
        string workingDirectory = "/tmp/harbor") => new(
        agent,
        TestModel,
        tools,
        Array.Empty<ContextFile>(),
        Array.Empty<SkillDescriptor>(),
        null,
        workingDirectory);

    private static ToolDescriptor Tool(string name, string description, string? snippet = null, params string[] guidelines) => new(
        ToolName.Create(name),
        name,
        description,
        JsonDocument.Parse("{}"),
        ExecutionMode.Parallel,
        snippet,
        guidelines);

    [Test]
    public async Task BuildAsync_IncludesEnvironmentSection()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>(), "/custom/dir");

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Environment");
        await Assert.That(prompt).Contains("Working directory");
        await Assert.That(prompt).Contains("/custom/dir");
        await Assert.That(prompt).Contains("Platform");
        await Assert.That(prompt).Contains("Today");
        await Assert.That(prompt).Contains("Model");
    }

    [Test]
    public async Task BuildAsync_IncludesAvailableToolsSection()
    {
        var builder = new SystemPromptBuilder();
        var tools = new[]
        {
            Tool("read", "Read a file", "read: Read a file from disk", "Use `read` before editing"),
            Tool("bash", "Run a shell command")
        };
        var ctx = Context(Agent(), tools);

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Available Tools");
        await Assert.That(prompt).Contains("`read`");
        await Assert.That(prompt).Contains("Read a file from disk");
        await Assert.That(prompt).Contains("Use `read` before editing");
        await Assert.That(prompt).Contains("`bash`");
    }

    [Test]
    public async Task BuildAsync_NoTools_StillRendersToolsHeader()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>());

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Available Tools");
    }

    [Test]
    public async Task BuildAsync_IncludesAgentSpecificInstructions_WhenPresent()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent("Always be precise and respectful of the user's time."), Array.Empty<ToolDescriptor>());

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Additional Instructions");
        await Assert.That(prompt).Contains("Always be precise and respectful of the user's time.");
    }

    [Test]
    public async Task BuildAsync_OmitsAdditionalInstructions_WhenAgentHasNone()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(append: null), Array.Empty<ToolDescriptor>());

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt.Contains("## Additional Instructions")).IsFalse();
    }

    [Test]
    public async Task BuildAsync_IncludesContextFiles_WhenProvided()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>()) with
        {
            ContextFiles = new[]
            {
                new ContextFile("/repo/AGENTS.md", "## Conventions\nUse TUnit for tests.")
            }
        };

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Project Context");
        await Assert.That(prompt).Contains("<project_context>");
        await Assert.That(prompt).Contains("/repo/AGENTS.md");
        await Assert.That(prompt).Contains("Use TUnit for tests.");
    }

    [Test]
    public async Task BuildAsync_IncludesSkills_WhenProvided()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>()) with
        {
            Skills = new[]
            {
                new SkillDescriptor("dotnet-testing", "How to write TUnit tests", "/skills/dotnet-testing.md")
            }
        };

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Available Skills");
        await Assert.That(prompt).Contains("dotnet-testing");
        await Assert.That(prompt).Contains("/skills/dotnet-testing.md");
        await Assert.That(prompt).Contains("<available_skills>");
    }

    [Test]
    public async Task BuildAsync_IncludesMcpInstructions_WhenProvided()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>()) with
        {
            McpInstructions = "Use the `mcp__filesystem` tool for filesystem access."
        };

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## MCP Servers");
        await Assert.That(prompt).Contains("mcp__filesystem");
    }

    [Test]
    public async Task BuildAsync_BasePromptMentionsHarborAndTools()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>());

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("Harbor");
        await Assert.That(prompt).Contains("tools");
    }
}
