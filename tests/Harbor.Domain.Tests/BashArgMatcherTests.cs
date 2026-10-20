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
    // #1770: the "flagged" copies differed only in the command lines — every
    // assert was the same `IsTrue` over a list of vectors. The vectors are one
    // '|'-separated string per row, because an attribute argument cannot
    // carry an implicitly-typed array, and every row keeps its original test
    // name as the display name.
    [Test]
    [Arguments("cat \"x$(rm -rf ~)\"|echo \"$(id -u)\"|cat \"$((1+1))\"", DisplayName = "HasShellMetacharacters_DoubleQuotedCommandSubstitution_IsFlagged")]
    [Arguments("echo \"a`b\"|echo \"run `whoami` now\"", DisplayName = "HasShellMetacharacters_DoubleQuotedBackticks_AreFlagged")]
    [Arguments("grep \"a\\.b\" x|echo \"C:\\temp\"", DisplayName = "HasShellMetacharacters_DoubleQuotedBackslashEscapes_AreFlagged")]
    [Arguments("echo \"abc|echo 'abc|cat abc\\", DisplayName = "HasShellMetacharacters_UnterminatedQuotesOrTrailingEscape_AreStillFlagged")]
    public async Task HasShellMetacharacters_DangerousVectors_AreFlagged(string commands)
    {
        foreach (string command in commands.Split('|'))
            await Assert.That(BashArgMatcher.HasShellMetacharacters(command)).IsTrue();
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
    // #1770: the three wildcard-rule evaluations differed only in the command
    // and the expected action — the assert never changed. They are the table
    // now, and every row keeps its original test name as the display name.
    [Test]
    [Arguments("cat \"x$(rm -rf ~)\"", PermissionAction.Ask, DisplayName = "Evaluate_DoubleQuotedCommandSubstitution_EscapesAllowRuleToAsk")]
    [Arguments("cat 'x$(rm -rf ~)'", PermissionAction.Allow, DisplayName = "Evaluate_SingleQuotedCommandSubstitution_AllowedByWildcardAllow")]
    [Arguments("cat README.md", PermissionAction.Allow, DisplayName = "Evaluate_PlainArgs_StillAllowedByWildcardAllow")]
    public async Task Evaluate_WildcardAllowRule_ResolvesToAction(string command, PermissionAction expected)
    {
        var action = CreateCatWildcardRuleset().Evaluate("bash", command);
        await Assert.That(action).IsEqualTo(expected);
    }

    [Test]
    public async Task Evaluate_DoubleQuotedBackticksAndBackslashes_EscapeAllowRuleToAsk()
    {
        var ruleset = CreateCatWildcardRuleset();
        await Assert.That(ruleset.Evaluate("bash", "cat \"x`y`\"")).IsEqualTo(PermissionAction.Ask);
        await Assert.That(ruleset.Evaluate("bash", "cat \"a\\.b\"")).IsEqualTo(PermissionAction.Ask);
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
