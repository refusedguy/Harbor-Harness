using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
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

    // #1770: the bash scenarios used to be one copy-pasted method per command.
    // The assert never changed — only the command string did — so the commands
    // are the table now and the expectation stays in one place. Every row
    // keeps its original test name as the case display name.
    [Test]
    [Arguments("cat setup.sh; rm -rf ~", DisplayName = "CheckAsync_BashAllowRuleWithChainedDestructiveTail_IsNotAllow")]
    [Arguments("git diff | sh", DisplayName = "CheckAsync_BashAllowRuleWithPipedShellExecution_IsNotAllow")]
    [Arguments("cat `whoami`.log", DisplayName = "CheckAsync_BashAllowRuleWithBacktickSubstitution_IsNotAllow")]
    [Arguments("cat README.md\nrm -rf ~/notes", DisplayName = "CheckAsync_BashMultilineCommandUnderAllowRule_IsNotAllow")]
    public async Task CheckAsync_BashDestructiveCommand_IsNotAllow(string command)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("cat README.md", DisplayName = "CheckAsync_BenignCatCommand_StillAllowed")]
    [Arguments("git diff HEAD~1", DisplayName = "CheckAsync_BenignGitDiffCommand_StillAllowed")]
    public async Task CheckAsync_BenignBashCommand_IsAllowed(string command)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { command });

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    [Arguments("rm -fr /", DisplayName = "CheckAsync_RmRfSlashWithSwappedFlags_IsDeniedWithoutPrompt")]
    [Arguments("cd / && rm -rf .", DisplayName = "CheckAsync_CompoundRmRfDotPath_IsDeniedWithoutPrompt")]
    [Arguments("/usr/bin/sudo ls", DisplayName = "CheckAsync_SudoViaAbsolutePath_IsDeniedWithoutPrompt")]
    [Arguments("sudo ls", DisplayName = "CheckAsync_DirectSudoCommand_IsDeniedWithoutPrompt")]
    [Arguments("rm -rf /", DisplayName = "CheckAsync_DirectRmRfRootCommand_IsDeniedWithoutPrompt")]
    public async Task CheckAsync_DestructiveCommand_IsDeniedWithoutPrompt(string command)
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

    [Test]
    [Arguments("src/../../../etc/passwd", "pwned", DisplayName = "CheckAsync_WriteTraversalPathEscapingSrc_IsNotAllow")]
    [Arguments("/etc/harbor-redteam-probe.txt", "pwned", DisplayName = "CheckAsync_WriteAbsolutePathOutsideWorkspace_IsNotAllow")]
    public async Task CheckAsync_WritePathOutsideWorkspace_IsNotAllow(string path, string content)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path, content });

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

    // #1770: one table for the tools whose traversal guard keys on the "path"
    // argument — the argument shape (and therefore the policy decision) is the
    // same for all four, only the tool and the path differ.
    [Test]
    [Arguments("patch", "src/../../../etc/cron.d/evil", DisplayName = "CheckAsync_PatchTraversalArgPath_IsNotAllow")]
    [Arguments("tree", "../../etc", DisplayName = "CheckAsync_TreeTraversalArgPath_IsNotAllow")]
    [Arguments("notebook", "../../etc/evil.ipynb", DisplayName = "CheckAsync_NotebookTraversalArgPath_IsNotAllow")]
    [Arguments("mcp", "../../etc", DisplayName = "CheckAsync_McpTraversalArgPath_IsNotAllow")]
    public async Task CheckAsync_TraversalArgPath_IsNotAllow(string tool, string path)
    {
        var svc = CreateService(PermissionRuleset.Default);
        var args = Args(new { path });

        var result = await svc.CheckAsync("code", tool, args);

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
}
