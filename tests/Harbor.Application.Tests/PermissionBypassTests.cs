using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Harbor.Application.Permissions;
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
    [Arguments("cat setup.sh; rm -rf ~", "chained destructive tail")]
    [Arguments("git diff | sh", "piped shell execution")]
    [Arguments("cat `whoami`.log", "backtick substitution")]
    [Arguments("cat README.md\nrm -rf ~/notes", "multiline command")]
    public async Task CheckAsync_BashAllowRule_BypassAttempts_AreNotAllow(string command, string _)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("cat README.md", "benign cat command")]
    [Arguments("git diff HEAD~1", "benign git diff command")]
    public async Task CheckAsync_BashBenignCommands_StillAllowed(string command, string _)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("write", "src/../../../etc/passwd", "traversal escaping src")]
    [Arguments("write", "/etc/harbor-redteam-probe.txt", "absolute path outside workspace")]
    [Arguments("edit", "src/../../secrets.env", "traversal escaping src")]
    [Arguments("patch", "src/../../../etc/cron.d/evil", "traversal arg path")]
    [Arguments("tree", "../../etc", "traversal arg path")]
    [Arguments("notebook", "../../etc/evil.ipynb", "traversal arg path")]
    [Arguments("mcp", "../../etc", "traversal arg path")]
    public async Task CheckAsync_FileTool_UnsafePaths_AreNotAllowed(string tool, string path, string _)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path });

        var result = await svc.CheckAsync("code", tool, args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("write", "src/feature/new.ts", "normal src path")]
    public async Task CheckAsync_WriteNormalSrcPath_StillAllowed(string tool, string path, string _)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path });

        var result = await svc.CheckAsync("code", tool, args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("ripgrep", "secret", "../../etc", "traversal arg path")]
    public async Task CheckAsync_Ripgrep_UnsafePath_IsNotAllowed(string tool, string pattern, string path, string _)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { pattern, path });

        var result = await svc.CheckAsync("code", tool, args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("rm -fr /", "RmRfSlashWithSwappedFlags")]
    [Arguments("cd / && rm -rf .", "CompoundRmRfDotPath")]
    [Arguments("/usr/bin/sudo ls", "SudoViaAbsolutePath")]
    [Arguments("sudo ls", "DirectSudoCommand")]
    [Arguments("rm -rf /", "DirectRmRfRootCommand")]
    public async Task CheckAsync_DestructiveBash_IsDeniedWithoutPrompt(string command, string _)
    {
        int prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest req, CancellationToken ct)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var svc = CreateService(PermissionRuleset.Default, CountingAsker);
        var args = Args(new { command });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }
}
