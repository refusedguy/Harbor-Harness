using Harbor.Abstractions.Permissions;
namespace Harbor.Domain.Tests;
public class BashArgMatcherTests
{
    [Test]
    public async Task HasShellMetacharacters_PlainArgsWithoutQuotes_ReturnsFalse()
    {
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat README.md")).IsFalse();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("ls -la src/")).IsFalse();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("git status")).IsFalse();
    }

    [Test]
    public async Task HasShellMetacharacters_SingleQuotedSpecialChars_StaysSafe()
    {
        // POSIX single quotes are fully literal: nothing inside them executes.
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat 'a;b|c&d'")).IsFalse();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat 'x$(rm -rf ~)'")).IsFalse();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo '`whoami`'")).IsFalse();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo 'a\\b'")).IsFalse();
    }

    /// <summary>
    ///     Security fix (A1-bash-arg-matcher-security): POSIX shells execute $()
    ///     command substitution INSIDE double quotes. Before the fix,
    ///     <c>cat "x$(rm -rf ~)"</c> passed an Allow rule like <c>bash "cat *"</c>.
    /// </summary>
    [Test]
    public async Task HasShellMetacharacters_DoubleQuotedCommandSubstitution_IsFlagged()
    {
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat \"x$(rm -rf ~)\"")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo \"$(id -u)\"")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat \"$((1+1))\"")).IsTrue();
    }

    [Test]
    public async Task HasShellMetacharacters_DoubleQuotedBackticks_AreFlagged()
    {
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo \"a`b\"")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo \"run `whoami` now\"")).IsTrue();
    }

    [Test]
    public async Task HasShellMetacharacters_DoubleQuotedBackslashEscapes_AreFlagged()
    {
        await Assert.That(BashArgMatcher.HasShellMetacharacters("grep \"a\\.b\" x")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo \"C:\\temp\"")).IsTrue();
    }

    [Test]
    public async Task HasShellMetacharacters_UnquotedMetachars_AreStillFlagged()
    {
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat f; ls")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat f | rm -rf ~")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("true && rm -rf .")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo $(id)")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo `id`")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat f > /etc/passwd")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("sort < f")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat f\nrm -rf ~")).IsTrue();
    }

    [Test]
    public async Task HasShellMetacharacters_UnterminatedQuotesOrTrailingEscape_AreStillFlagged()
    {
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo \"abc")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("echo 'abc")).IsTrue();
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat abc\\")).IsTrue();
    }

    [Test]
    public async Task HasShellMetacharacters_BackslashEscapeOutsideQuotes_IsNotFlagged()
    {
        // shlex behavior: "\<space>" escapes the space, no metacharacter present.
        await Assert.That(BashArgMatcher.HasShellMetacharacters("cat my\\ file.txt")).IsFalse();
    }

    private static PermissionRuleset CreateCatWildcardRuleset() => new(new[]
    {
        new PermissionRule("bash", "cat *", PermissionAction.Allow),
        new PermissionRule("bash", "*", PermissionAction.Ask)
    });

    /// <summary>
    ///     The exploit this fix closes: double-quoted command substitution must not be
    ///     silently authorized by a wildcard Allow rule.
    /// </summary>
    [Test]
    public async Task Evaluate_DoubleQuotedCommandSubstitution_EscapesAllowRuleToAsk()
    {
        var action = CreateCatWildcardRuleset().Evaluate("bash", "cat \"x$(rm -rf ~)\"");
        await Assert.That(action).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task Evaluate_DoubleQuotedBackticksAndBackslashes_EscapeAllowRuleToAsk()
    {
        var ruleset = CreateCatWildcardRuleset();
        await Assert.That(ruleset.Evaluate("bash", "cat \"x`y`\"")).IsEqualTo(PermissionAction.Ask);
        await Assert.That(ruleset.Evaluate("bash", "cat \"a\\.b\"")).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task Evaluate_SingleQuotedCommandSubstitution_AllowedByWildcardAllow()
    {
        var action = CreateCatWildcardRuleset().Evaluate("bash", "cat 'x$(rm -rf ~)'");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Evaluate_PlainArgs_StillAllowedByWildcardAllow()
    {
        var action = CreateCatWildcardRuleset().Evaluate("bash", "cat README.md");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    /// <summary>
    ///     Segmentation must propagate danger flags: each pipeline/list segment keeps its
    ///     quoting verbatim, so a dangerous double-quoted construct in ANY segment
    ///     escalates the whole command to Ask.
    /// </summary>
    [Test]
    public async Task Evaluate_CompoundSegmentWithDoubleQuoteDanger_EscalatesToAsk()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("bash", "git status", PermissionAction.Allow),
            new PermissionRule("bash", "*", PermissionAction.Ask)
        });
        await Assert.That(ruleset.Evaluate("bash", "git status && cat \"x$(rm -rf ~)\""))
            .IsEqualTo(PermissionAction.Ask);
        await Assert.That(ruleset.Evaluate("bash", "git status; echo \"a`b`\""))
            .IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task GetDenyMatchTargets_PropagatesQuotedSegmentsVerbatim()
    {
        var targets = BashArgMatcher.GetDenyMatchTargets("cat \"my file.txt\" && sudo ls");
        await Assert.That(targets.Contains("sudo ls")).IsTrue();
        await Assert.That(targets.Contains("sudo")).IsTrue();
        await Assert.That(targets.Contains("cat my file.txt")).IsTrue();
    }

    [Test]
    public async Task IsDestructiveCommand_DetectsQuotedRootTarget_AndIgnoresSafePaths()
    {
        await Assert.That(BashArgMatcher.IsDestructiveCommand("rm -rf \"/\"")).IsTrue();
        await Assert.That(BashArgMatcher.IsDestructiveCommand("rm -rf '~'")).IsTrue();
        await Assert.That(BashArgMatcher.IsDestructiveCommand("rm -rf build/")).IsFalse();
    }
}
