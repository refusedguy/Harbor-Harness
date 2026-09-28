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
///     Tests for the peer-supervision prompt recipe (#165): the
///     <c>## Peer Supervision</c> section renders only when the supervision
///     tools are resolved for the turn.
/// </summary>
public class SystemPromptSupervisionTests
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

    private static AgentDefinition Agent() => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent.",
        "test-model",
        "test",
        PermissionRuleset.Default);

    private static SystemPromptContext Context(IReadOnlyList<ToolDescriptor> tools) => new(
        Agent(),
        TestModel,
        tools,
        Array.Empty<ContextFile>(),
        Array.Empty<SkillDescriptor>(),
        null,
        "~/.cache/harbor-tests");

    private static ToolDescriptor Tool(string name) => new(
        ToolName.Create(name),
        name,
        name + " tool",
        JsonDocument.Parse("{}"),
        ExecutionMode.Parallel,
        null,
        Array.Empty<string>());

    [Test]
    public async Task BuildAsync_WithSupervisionTools_IncludesRecipe()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(new[] { Tool("read"), Tool("session_read"), Tool("session_steer") });

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Peer Supervision");
        await Assert.That(prompt).Contains("session_read");
        await Assert.That(prompt).Contains("session_steer");
        await Assert.That(prompt).Contains("read → verdict");
    }

    [Test]
    public async Task BuildAsync_WithoutSupervisionTools_OmitsRecipe()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(new[] { Tool("read"), Tool("bash") });

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt.Contains("## Peer Supervision")).IsFalse();
    }
}
