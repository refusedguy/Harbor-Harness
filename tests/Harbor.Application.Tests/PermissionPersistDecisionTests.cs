using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public class PermissionPersistDecisionTests
{
    private static JsonElement Args(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static PermissionService CreateService(
        PermissionRuleset ruleset,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> asker)
    {
        var agent = new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "Red-team persist-decision harness agent",
            "test-model",
            "test",
            ruleset);
        var registry = new FakeAgentRegistry(agent);
        return new PermissionService(registry, NullLogger<PermissionService>.Instance, asker);
    }

    [Test]
    public async Task CheckAsync_PersistedAllowDecision_SecondCallDoesNotPromptAgain()
    {
        int prompts = 0;
        Task<PermissionResponse> Asker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Allow, true));
        }

        var svc = CreateService(
            new PermissionRuleset(new PermissionRule[] { new("write", "*", PermissionAction.Ask) }),
            Asker);

        var first = await svc.CheckAsync("code", "write", Args(new { path = "/repo/file.txt" }));
        var second = await svc.CheckAsync("code", "write", Args(new { path = "/repo/file.txt" }));

        await Assert.That(first.Value.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(second.Value.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(prompts).IsEqualTo(1);
        var ruleset = svc.GetRuleset("code");
        await Assert.That(ruleset.Evaluate("write", "/repo/file.txt")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_NonPersistedAllowDecision_PromptsAgainOnNextCall()
    {
        int prompts = 0;
        Task<PermissionResponse> Asker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Allow, false));
        }

        var svc = CreateService(
            new PermissionRuleset(new PermissionRule[] { new("write", "*", PermissionAction.Ask) }),
            Asker);

        var first = await svc.CheckAsync("code", "write", Args(new { path = "/repo/one.txt" }));
        var second = await svc.CheckAsync("code", "write", Args(new { path = "/repo/two.txt" }));

        await Assert.That(first.Value.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(second.Value.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(prompts).IsEqualTo(2);
    }

    [Test]
    public async Task CheckAsync_PersistedDenyDecision_SecondCallDeniedWithoutPrompt()
    {
        int prompts = 0;
        Task<PermissionResponse> Asker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, true));
        }

        var svc = CreateService(
            new PermissionRuleset(new PermissionRule[] { new("bash", "*", PermissionAction.Ask) }),
            Asker);

        var args = Args(new { command = "make build" });
        var first = await svc.CheckAsync("code", "bash", args);
        var second = await svc.CheckAsync("code", "bash", args);

        await Assert.That(first.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(second.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(1);
        var ruleset = svc.GetRuleset("code");
        await Assert.That(ruleset.Evaluate("bash", "make build")).IsEqualTo(PermissionAction.Deny);
    }
}
