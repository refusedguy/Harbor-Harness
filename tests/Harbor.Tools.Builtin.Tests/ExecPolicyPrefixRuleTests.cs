using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     C1 (sprint 6): bash Allow prefix rules match TOKEN-wise over
///     BashArgMatcher argv parsing — exact case-sensitive leading tokens,
///     trailing "*" opens the argument tail. Deny semantics unchanged.
/// </summary>
public class ExecPolicyPrefixRuleTests
{
    private static PermissionAction Eval(string command)
        => PermissionRuleset.Default.Evaluate("bash", command);

    [Test]
    public async Task OpenEndedPrefix_AllowsRealInvocations()
    {
        await Assert.That(Eval("git log -5 --oneline")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("git status --porcelain")).IsNotEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("cat README.md")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("ls -la src/")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Prefix_DoesNotMatchForeignTokens()
    {
        // "cat *" must not allow look-alike binaries or case games.
        await Assert.That(Eval("catalog --help")).IsNotEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("CAT /etc/shadow")).IsNotEqualTo(PermissionAction.Allow);
        // "git *" is not "gitk" nor "GIT".
        await Assert.That(Eval("git push --force origin main")).IsNotEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("GIT push")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task ExactRule_RequiresExactArgv()
    {
        // Default ruleset: "git status" (exact) — with flags it no longer
        // matches the exact rule, and no open-ended git rule exists → Ask.
        await Assert.That(Eval("git status")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("git status --porcelain")).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task QuotedArguments_TokenizeBeforeMatching()
    {
        await Assert.That(Eval("grep 'my pattern' file.txt")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("find . -name '*.log'")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Metacharacters_StillEscalateToAsk()
    {
        await Assert.That(Eval("git status; rm -rf ~")).IsNotEqualTo(PermissionAction.Allow);
        await Assert.That(Eval("cat README.md | sh")).IsNotEqualTo(PermissionAction.Allow);
    }
}
