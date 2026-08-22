using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Tests.Fakes;
using Harbor.Core.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public class PermissionBypassTests
{
    private static JsonElement Args(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static PermissionService CreateService(
        PermissionRuleset ruleset,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? asker = null)
    {
        var agent = new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "Red-team permission harness agent",
            "test-model",
            "test",
            ruleset);
        var registry = new FakeAgentRegistry(agent);
        return new PermissionService(registry, NullLogger<PermissionService>.Instance, asker);
    }

    [Test]
    public async Task CheckAsync_BashAllowRuleWithChainedDestructiveTail_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command = "cat setup.sh; rm -rf ~" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_BashAllowRuleWithPipedShellExecution_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command = "git diff | sh" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_BashAllowRuleWithBacktickSubstitution_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command = "cat `whoami`.log" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_BashMultilineCommandUnderAllowRule_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command = "cat README.md\nrm -rf ~/notes" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_BenignCatCommand_StillAllowed()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command = "cat README.md" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_BenignGitDiffCommand_StillAllowed()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command = "git diff HEAD~1" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_RmRfSlashWithSwappedFlags_IsDeniedWithoutPrompt()
    {
        int prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var svc = CreateService(PermissionRuleset.Default, CountingAsker);
        var args = Args(new { command = "rm -fr /" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_CompoundRmRfDotPath_IsDeniedWithoutPrompt()
    {
        int prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var svc = CreateService(PermissionRuleset.Default, CountingAsker);
        var args = Args(new { command = "cd / && rm -rf ." });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_SudoViaAbsolutePath_IsDeniedWithoutPrompt()
    {
        int prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var svc = CreateService(PermissionRuleset.Default, CountingAsker);
        var args = Args(new { command = "/usr/bin/sudo ls" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_DirectSudoCommand_IsDeniedWithoutPrompt()
    {
        int prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var svc = CreateService(PermissionRuleset.Default, CountingAsker);
        var args = Args(new { command = "sudo ls" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_DirectRmRfRootCommand_IsDeniedWithoutPrompt()
    {
        int prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var svc = CreateService(PermissionRuleset.Default, CountingAsker);
        var args = Args(new { command = "rm -rf /" });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_WriteTraversalPathEscapingSrc_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "src/../../../etc/passwd", content = "pwned" });

        var result = await svc.CheckAsync("code", "write", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_WriteAbsolutePathOutsideWorkspace_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "/etc/harbor-redteam-probe.txt", content = "pwned" });

        var result = await svc.CheckAsync("code", "write", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_WriteNormalSrcPath_StillAllowed()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "src/feature/new.ts", content = "ok" });

        var result = await svc.CheckAsync("code", "write", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_EditTraversalPathEscapingSrc_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "src/../../secrets.env", oldString = "a", newString = "b" });

        var result = await svc.CheckAsync("code", "edit", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_PatchTraversalArgPath_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "src/../../../etc/cron.d/evil" });

        var result = await svc.CheckAsync("code", "patch", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_TreeTraversalArgPath_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "../../etc" });

        var result = await svc.CheckAsync("code", "tree", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_RipgrepTraversalArgPath_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { pattern = "secret", path = "../../etc" });

        var result = await svc.CheckAsync("code", "ripgrep", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_NotebookTraversalArgPath_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "../../etc/evil.ipynb" });

        var result = await svc.CheckAsync("code", "notebook", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_McpTraversalArgPath_IsNotAllow()
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path = "../../etc" });

        var result = await svc.CheckAsync("code", "mcp", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }
}
