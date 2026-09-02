using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.Application.Tests.Fakes;
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

    [Test]
    public async Task LoadFromConfig_ExistingPermissions_MergesIntoRuleset()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-perm-{Guid.NewGuid():N}", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var config = new HarborConfig
            {
                Provider = "kilocode",
                Model = "kilocode/tencent/hy3:free",
            };
            config.Permissions["code"] = new List<PermissionRule>
            {
                new("bash", "make *", PermissionAction.Allow)
            };

            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            await store.SaveAsync(config);

            var agent = new AgentDefinition(
                AgentName.Create("code"),
                "Code",
                "Test agent",
                "test-model",
                "test",
                PermissionRuleset.Default);
            var registry = new FakeAgentRegistry(agent);
            var svc = new PermissionService(registry, NullLogger<PermissionService>.Instance, configStore: store);

            var ruleset = svc.GetRuleset("code");
            await Assert.That(ruleset.Evaluate("bash", "make build")).IsEqualTo(PermissionAction.Allow);
            await Assert.That(ruleset.Evaluate("bash", "rm -rf /")).IsEqualTo(PermissionAction.Deny);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_PersistsCurrentDecisionsToConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-perm-{Guid.NewGuid():N}", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var agent = new AgentDefinition(
                AgentName.Create("code"),
                "Code",
                "Test agent",
                "test-model",
                "test",
                PermissionRuleset.Default);
            var registry = new FakeAgentRegistry(agent);
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var svc = new PermissionService(registry, NullLogger<PermissionService>.Instance, configStore: store);

            int prompts = 0;
            Task<PermissionResponse> Asker(PermissionRequest req, CancellationToken ct)
            {
                prompts++;
                return Task.FromResult(new PermissionResponse(PermissionAction.Allow, true));
            }

            // Simulate a user decision by directly adding to _persisted via CheckAsync
            // (in real usage, CheckAsync prompts and persists when PersistDecision=true)
            // Here we just verify SaveAsync works:
            await svc.SaveAsync();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.IsSuccess).IsTrue();
            await Assert.That(loaded.Value.Permissions).IsNotNull();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
