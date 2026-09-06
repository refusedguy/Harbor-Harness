using System.Text.Json;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public class PermissionBypassTests
{
    private static PermissionService CreateService(
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? asker = null) =>
        new(
            new FakeAgentRegistry(TestAgents.WithPermission(ruleset: PermissionRuleset.Default)),
            NullLogger<PermissionService>.Instance,
            asker);

    [Test]
    [Arguments("cat setup.sh; rm -rf ~")]
    [Arguments("git diff | sh")]
    [Arguments("cat `whoami`.log")]
    [Arguments("cat README.md\nrm -rf ~/notes")]
    public async Task CheckAsync_BashAllowRule_BypassAttempts_AreNotAllow(string command)
    {
        var args = JsonDocument.Parse($$"""{"command":{{JsonSerializer.Serialize(command)}}}""").RootElement;

        var result = await CreateService().CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("cat README.md")]
    [Arguments("git diff HEAD~1")]
    public async Task CheckAsync_BashBenignCommands_StillAllowed(string command)
    {
        var args = JsonDocument.Parse($$"""{"command":{{JsonSerializer.Serialize(command)}}}""").RootElement;

        var result = await CreateService().CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("write", "src/../../../etc/passwd")]
    [Arguments("write", "/etc/harbor-redteam-probe.txt")]
    [Arguments("edit", "src/../../secrets.env")]
    [Arguments("patch", "src/../../../etc/cron.d/evil")]
    [Arguments("tree", "../../etc")]
    [Arguments("notebook", "../../etc/evil.ipynb")]
    [Arguments("mcp", "../../etc")]
    public async Task CheckAsync_FileTool_UnsafePaths_AreNotAllowed(string tool, string path)
    {
        var args = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(path)}}}""").RootElement;

        var result = await CreateService().CheckAsync("code", tool, args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("write", "src/feature/new.ts")]
    public async Task CheckAsync_WriteNormalSrcPath_StillAllowed(string tool, string path)
    {
        var args = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(path)}}}""").RootElement;

        var result = await CreateService().CheckAsync("code", tool, args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("ripgrep", "secret", "../../etc")]
    public async Task CheckAsync_Ripgrep_UnsafePath_IsNotAllowed(string tool, string pattern, string path)
    {
        var args = JsonDocument.Parse(
            $$"""{"pattern":{{JsonSerializer.Serialize(pattern)}},"path":{{JsonSerializer.Serialize(path)}}}""").RootElement;

        var result = await CreateService().CheckAsync("code", tool, args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("rm -fr /")]
    [Arguments("cd / && rm -rf .")]
    [Arguments("/usr/bin/sudo ls")]
    [Arguments("sudo ls")]
    [Arguments("rm -rf /")]
    public async Task CheckAsync_DestructiveBash_IsDeniedWithoutPrompt(string command)
    {
        var prompts = 0;
        Task<PermissionResponse> CountingAsker(PermissionRequest _, CancellationToken __)
        {
            prompts++;
            return Task.FromResult(new PermissionResponse(PermissionAction.Deny, false));
        }

        var args = JsonDocument.Parse($$"""{"command":{{JsonSerializer.Serialize(command)}}}""").RootElement;
        var result = await CreateService(CountingAsker).CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(prompts).IsEqualTo(0);
    }
}
