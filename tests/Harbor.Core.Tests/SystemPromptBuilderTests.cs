using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Sessions;
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
        string workingDirectory = "~/.cache/harbor-tests") => new(
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
        await Assert.That(prompt).Contains("Model");
    }

    /// <summary>
    ///     #814: the environment block used to carry <c>- Today: </c>, read
    ///     from the wall clock inside <c>BuildAsync</c>. This asserts the line is
    ///     GONE, and says why, because the deletion is a decision and the next
    ///     reader of this file will otherwise read the missing assertion as an
    ///     oversight and helpfully restore a "useful context detail".
    /// </summary>
    /// <remarks>
    ///     The block is sliced rather than the whole prompt on purpose: a
    ///     project-context file that happens to contain the word "Today" is the
    ///     USER's content arriving through the context, which is exactly what
    ///     this test must keep allowing. The invariant is about what the
    ///     BUILDER injects, not about what the context carries.
    /// </remarks>
    [Test]
    public async Task BuildAsync_EnvironmentBlock_InjectsNoClockRead()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>(), "/custom/dir");

        string environment = EnvironmentBlockOf(await builder.BuildAsync(ctx));

        await Assert.That(environment).IsNotEmpty()
            .Because("this test grades the environment block; an empty slice means the header moved "
                   + "or the block stopped rendering, and the assertion below would pass on nothing");

        await Assert.That(environment).DoesNotContain("Today")
            .Because(
                "the date was the one line of this block that came from neither the context nor a "
                + "machine fact, so no cache key derived from the context could cover it: "
                + "CachingSystemPromptBuilder keeps entries for the life of the process, and a session "
                + "left open across 00:00 UTC told the model it was still yesterday — a stale answer "
                + "the model can act on, not only a cache artefact. The value was not even the user's "
                + "today: it was a UTC date with no zone on it. A date the model genuinely needs "
                + "arrives as a SystemPromptContext member, where the key reaches it by construction. "
                + "Block: " + environment);
    }

    /// <summary>
    ///     The <c>## Environment</c> block, up to the blank line that ends it.
    ///     Empty when the header is absent, so a caller asserting on the slice
    ///     fails on the emptiness rather than on a substring it never looked at.
    /// </summary>
    private static string EnvironmentBlockOf(string prompt)
    {
        const string header = "## Environment";

        int start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        int end = prompt.IndexOf("\n\n", start, StringComparison.Ordinal);
        return end < 0 ? prompt[start..] : prompt[start..end];
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

    /// <summary>
    ///     #577: the empty-tools arm renders a DIFFERENT sentence from the
    ///     non-empty arm, and that sentence was the one part of the section no
    ///     test in the repository asserted — the test above pins only the header,
    ///     which both arms share. So the guidance the model actually reads on a
    ///     zero-tool turn could change with every test still green, and the
    ///     failure mode is a model inventing tool calls against a header that
    ///     promises some.
    /// </summary>
    [Test]
    public async Task BuildAsync_NoTools_SaysThereAreNone()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), Array.Empty<ToolDescriptor>());

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains(SystemPromptBuilder.NoToolsGuidance);
    }

    /// <summary>
    ///     #577: the other arm of the same branch. With tools resolved, the
    ///     "answer from knowledge only" text must NOT appear — it is the opposite
    ///     instruction, and the header alone does not distinguish the two arms.
    /// </summary>
    [Test]
    public async Task BuildAsync_WithTools_OmitsTheNoToolsGuidance()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Agent(), new[] { Tool("read", "Read a file") });

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt.Contains(SystemPromptBuilder.NoToolsGuidance)).IsFalse();
    }

    /// <summary>
    ///     #577: the guideline cap is a budget decision — it decides how much of
    ///     every tool's guidance reaches the model on every turn. It was the bare
    ///     literal <c>3</c>; nothing named it, so raising it to 30 changed the
    ///     prompt and nothing went red.
    /// </summary>
    [Test]
    public async Task BuildAsync_KeepsAtMostMaxGuidelinesPerTool()
    {
        var builder = new SystemPromptBuilder();
        var tooMany = Enumerable.Range(1, SystemPromptBuilder.MaxGuidelinesPerTool + 2)
            .Select(i => $"guideline number {i}")
            .ToArray();
        var tool = Tool("read", "Read a file", "read: Read a file from disk", tooMany);
        var ctx = Context(Agent(), new[] { tool });

        string prompt = await builder.BuildAsync(ctx);

        // The cap counts guidelines that SURVIVED the length filter, so a tool
        // with 5 short guidelines must contribute exactly MaxGuidelinesPerTool of
        // them — not all 5, and not the first 3 in some other sense.
        for (int i = 1; i <= SystemPromptBuilder.MaxGuidelinesPerTool; i++)
        {
            await Assert.That(prompt).Contains($"guideline number {i}");
        }

        await Assert.That(prompt.Contains($"guideline number {SystemPromptBuilder.MaxGuidelinesPerTool + 1}"))
            .IsFalse()
            .Because("the cap is a budget decision about what the model reads on every turn; raising it "
                   + "must be a visible change to a named policy, not an edit to a literal nobody names");
    }

    /// <summary>
    ///     #577: the per-guideline length ceiling, which was the bare literal
    ///     <c>160</c>. A guideline at the ceiling is kept and one past it is
    ///     dropped, so the boundary is pinned from both sides rather than by a
    ///     single "long ones are dropped" assertion that would pass for any
    ///     threshold.
    /// </summary>
    [Test]
    public async Task BuildAsync_DropsGuidelinesLongerThanMaxGuidelineLength()
    {
        var builder = new SystemPromptBuilder();

        // The ceiling applies to the WHOLE guideline, so the marker is part of
        // the budget rather than something added on top of it. Padding is
        // derived from the marker so the two strings cannot drift out of the
        // boundary this test exists to pin.
        static string Guideline(string marker, int totalLength) =>
            marker + new string('x', totalLength - marker.Length);

        string atCeiling = Guideline("KEEP-", SystemPromptBuilder.MaxGuidelineLength);
        string pastCeiling = Guideline("DROP-", SystemPromptBuilder.MaxGuidelineLength + 1);
        var tool = Tool("read", "Read a file", "read: Read a file from disk", atCeiling, pastCeiling);
        var ctx = Context(Agent(), new[] { tool });

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains(atCeiling);
        await Assert.That(prompt.Contains(pastCeiling)).IsFalse()
            .Because("the boundary is a policy about what the model reads, so it is pinned from BOTH "
                   + "sides: a guideline exactly at the ceiling is kept and one character past it is "
                   + "dropped. A single 'long ones are dropped' assertion would pass for any threshold");
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
