using Harbor.Abstractions.Permissions;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

public class PermissionPathRulesTests
{
    private static readonly PermissionRuleset Rules = PermissionRuleset.Default;

    private static PermissionAction Eval(string tool, string arg) => Rules.Evaluate(tool, arg);

    [Test]
    public async Task BashCatAllowRule_CommandChainTail_IsNotAutoAllowed()
    {
        await Assert.That(Eval("bash", "cat setup.sh; rm -rf ~")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task BashGitDiffAllowRule_PipeToShell_IsNotAutoAllowed()
    {
        await Assert.That(Eval("bash", "git diff | sh")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task BashCatAllowRule_BacktickSubstitution_IsNotAutoAllowed()
    {
        await Assert.That(Eval("bash", "cat `whoami`.log")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task BashAllowRule_MultilineCommand_IsNotAutoAllowed()
    {
        await Assert.That(Eval("bash", "cat README.md\nrm -rf ~/notes")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task BashDenyRules_FlagOrderVariant_IsDenied()
    {
        await Assert.That(Eval("bash", "rm -fr /")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task BashDenyRules_CompoundCommand_IsDenied()
    {
        await Assert.That(Eval("bash", "cd / && rm -rf .")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task BashDenyRules_AbsoluteSudoPath_IsDenied()
    {
        await Assert.That(Eval("bash", "/usr/bin/sudo ls")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task BashDenyRules_LiteralRmRfRoot_IsDenied()
    {
        await Assert.That(Eval("bash", "rm -rf /")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task BashDenyRules_PrefixedSudo_IsDenied()
    {
        await Assert.That(Eval("bash", "sudo ls")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task BashAllowRules_PlainCatCommand_IsAllowed()
    {
        await Assert.That(Eval("bash", "cat README.md")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task WritePathRule_TraversalSegments_IsNotAutoAllowed()
    {
        await Assert.That(Eval("write", "src/../../../etc/passwd")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task WritePathRule_AbsolutePathOutsideWorkspace_IsNotAutoAllowed()
    {
        await Assert.That(Eval("write", "/etc/harbor-redteam-probe.txt")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task WritePathRule_NormalSrcPath_IsAllowed()
    {
        await Assert.That(Eval("write", "src/feature/new.ts")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task EditPathRule_TraversalSegments_IsNotAutoAllowed()
    {
        await Assert.That(Eval("edit", "src/../../secrets.env")).IsNotEqualTo(PermissionAction.Allow);
    }
}
