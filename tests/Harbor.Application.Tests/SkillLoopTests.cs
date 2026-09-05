using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.TestKit;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Skill tool inside the agent loop (issue #16 E2E shape, scripted LLM):
///     the model emits a <c>skill</c> tool call, the loop executes
///     <see cref="SkillTool" /> against a temp skills root, and the skill body
///     lands in the turn's <see cref="ToolResultMessage" />.
/// </summary>
public class SkillLoopTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harbor-skill-loop").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir cleanup is best-effort */ }
        catch (UnauthorizedAccessException) { /* temp dir cleanup is best-effort */ }
    }

    [Test]
    public async Task RunAsync_SkillToolCall_LoadsSkillBodyIntoHistory()
    {
        string projectSkills = Path.Combine(_root, ".harbor", "skills");
        string skillDir = Path.Combine(projectSkills, "review");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
            "# Review\nFollow the house review checklist.\n");

        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "skill"),
                new ToolCallDeltaEvent("call-1", """{"name":"review"}"""),
                new StepFinishEvent(0, "stop", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "reviewed"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var skill = new SkillTool(null, NullLogger<SkillTool>.Instance, projectSkills, null);
        var loop = CreateLoop(client, new FakeToolRegistry(skill));
        var session = new TestSessionContext(
            Session.Create(_root, "code", "test", "test-model"), []);

        var result = await loop.RunAsync(session, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
        var toolResults = session.Messages.OfType<ToolResultMessage>().ToArray();
        await Assert.That(toolResults.Length).IsEqualTo(1);
        await Assert.That(string.Concat(toolResults[0].Results.Select(r => r.Output)))
            .Contains("house review checklist");
    }

    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"),
        "Code",
        "Skill loop harness agent",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static AgentLoop CreateLoop(ScriptedLlmClient client, FakeToolRegistry tools)
    {
        var agent = AllowAllAgent();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            tools,
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }
}
