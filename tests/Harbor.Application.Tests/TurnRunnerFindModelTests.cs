using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1516: TurnRunner.FindModel used to signal a missing model with a null
///     pointer that the caller wrapped in Maybe.From. It now answers
///     <c>Maybe&lt;ModelInfo&gt;</c> directly, so absence is a value at the type
///     level. These two tests pin the observable hit and miss outcomes through
///     the public AgentLoop seam; the miss path proves the None never collapses
///     back to a silent null (it becomes the "not found" failure).
/// </summary>
public class TurnRunnerFindModelTests
{
    private static AgentDefinition AgentWithModel(string model) => new(
        AgentName.Create("code"),
        "Code",
        "find-model harness",
        model,
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-find-model-tests", "code", "test", "test-model"));

    private static AgentLoop NewLoop(AgentDefinition agent)
    {
        // ScriptedLlmClient's catalog holds exactly one model: "test-model".
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "ok"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
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

    [Test]
    public async Task RunAsync_ModelPresent_ResolvesAndSucceeds()
    {
        // Hit: the catalog's only model matches the agent's model.
        AgentDefinition agent = AgentWithModel("test-model");

        Result result = await NewLoop(agent).RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task RunAsync_ModelAbsent_FailsWithNotFound()
    {
        // Miss: no catalog entry matches, so FindModel returns Maybe.None and
        // the caller's ToResult turns it into the same "not found" failure.
        AgentDefinition agent = AgentWithModel("missing-model");

        Result result = await NewLoop(agent).RunAsync(NewSession(), agent);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("Model 'missing-model' not found in provider 'test'.");
    }
}
