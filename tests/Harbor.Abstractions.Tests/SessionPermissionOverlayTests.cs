using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Issue #2665, items 1-2: the four session permission modes and the
///     in-memory overlay that carries "always for this session" decisions.
/// </summary>
public class SessionPermissionOverlayTests
{
    [Test]
    public async Task PermissionMode_HasTheFourOrderedValues()
    {
        await Assert.That((int)PermissionMode.Supervised).IsEqualTo(0);
        await Assert.That((int)PermissionMode.AutoAcceptEdits).IsEqualTo(1);
        await Assert.That((int)PermissionMode.Auto).IsEqualTo(2);
        await Assert.That((int)PermissionMode.FullAccess).IsEqualTo(3);
    }

    [Test]
    public async Task DefaultMode_IsSupervised_AndAddsNoAllowance()
    {
        var overlay = new SessionPermissionOverlay();

        await Assert.That(overlay.Mode).IsEqualTo(PermissionMode.Supervised);
        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "edit", "a.cs")).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    [Arguments(PermissionMode.Supervised, "edit", PermissionAction.Ask)]
    [Arguments(PermissionMode.AutoAcceptEdits, "edit", PermissionAction.Allow)]
    [Arguments(PermissionMode.AutoAcceptEdits, "write", PermissionAction.Allow)]
    [Arguments(PermissionMode.AutoAcceptEdits, "patch", PermissionAction.Allow)]
    [Arguments(PermissionMode.AutoAcceptEdits, "read", PermissionAction.Ask)]
    [Arguments(PermissionMode.AutoAcceptEdits, "bash", PermissionAction.Ask)]
    [Arguments(PermissionMode.AutoAcceptEdits, "unknown_tool", PermissionAction.Ask)]
    [Arguments(PermissionMode.Auto, "read", PermissionAction.Allow)]
    [Arguments(PermissionMode.Auto, "webfetch", PermissionAction.Allow)]
    [Arguments(PermissionMode.Auto, "bash", PermissionAction.Ask)]
    [Arguments(PermissionMode.Auto, "task", PermissionAction.Ask)]
    [Arguments(PermissionMode.FullAccess, "bash", PermissionAction.Allow)]
    public async Task Mode_AutoAllow_FollowsTheToolCategory(
        PermissionMode mode, string tool, PermissionAction expected)
    {
        var overlay = new SessionPermissionOverlay(mode);

        var action = overlay.Evaluate(PermissionRuleset.Empty, tool, "some-argument");

        await Assert.That(action).IsEqualTo(expected);
    }

    [Test]
    public async Task ModeSetter_ChangesTheAutomaticDecision()
    {
        var overlay = new SessionPermissionOverlay(PermissionMode.Supervised);
        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "edit", "a.cs")).IsEqualTo(PermissionAction.Ask);

        overlay.Mode = PermissionMode.AutoAcceptEdits;

        await Assert.That(overlay.Mode).IsEqualTo(PermissionMode.AutoAcceptEdits);
        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "edit", "a.cs")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task RulesetDeny_WinsOverFullAccess_AndOverSessionAllow()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("bash", "rm -rf /", PermissionAction.Deny)
        });
        var overlay = new SessionPermissionOverlay(PermissionMode.FullAccess);
        overlay.AllowForSession("bash", "*");

        var action = overlay.Evaluate(ruleset, "bash", "rm -rf /");

        await Assert.That(action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task RulesetAllow_IsHonouredInSupervised()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("read", "*", PermissionAction.Allow)
        });
        var overlay = new SessionPermissionOverlay(PermissionMode.Supervised);

        var action = overlay.Evaluate(ruleset, "read", "Program.cs");

        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task SessionDeny_WinsOverRulesetAllow_AndIsPatternScoped()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("read", "*", PermissionAction.Allow)
        });
        var overlay = new SessionPermissionOverlay();
        overlay.DenyForSession("read", "secret.txt");

        await Assert.That(overlay.Evaluate(ruleset, "read", "secret.txt")).IsEqualTo(PermissionAction.Deny);
        await Assert.That(overlay.Evaluate(ruleset, "read", "Program.cs")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task SessionAllow_PersistsUntilEndSession()
    {
        var overlay = new SessionPermissionOverlay(PermissionMode.Supervised);
        overlay.AllowForSession("bash", "git status *");

        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "bash", "git status --short"))
            .IsEqualTo(PermissionAction.Allow);
        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "bash", "git push"))
            .IsEqualTo(PermissionAction.Ask);

        overlay.EndSession();

        await Assert.That(overlay.Mode).IsEqualTo(PermissionMode.Supervised);
        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "bash", "git status --short"))
            .IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task SessionAllow_UsesCategoryNames()
    {
        var overlay = new SessionPermissionOverlay(PermissionMode.Supervised);
        overlay.AllowForSession("write", "*");

        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "edit", "a.cs"))
            .IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task EndSession_IsIdempotent()
    {
        var overlay = new SessionPermissionOverlay(PermissionMode.Auto);
        overlay.EndSession();
        overlay.EndSession();

        await Assert.That(overlay.Mode).IsEqualTo(PermissionMode.Supervised);
        await Assert.That(overlay.Evaluate(PermissionRuleset.Empty, "read", "a.cs"))
            .IsEqualTo(PermissionAction.Ask);
    }
}
