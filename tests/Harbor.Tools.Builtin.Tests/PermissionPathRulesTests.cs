using Harbor.Abstractions.Permissions;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

public class PermissionPathRulesTests
{
    private static readonly PermissionRuleset Rules = PermissionRuleset.Default;

    private static PermissionAction Eval(string tool, string arg) => Rules.Evaluate(tool, arg);

    [Test]
    [Arguments("bash", "cat setup.sh; rm -rf ~", PermissionAction.Allow, false)]
    [Arguments("bash", "git diff | sh", PermissionAction.Allow, false)]
    [Arguments("bash", "cat `whoami`.log", PermissionAction.Allow, false)]
    [Arguments("bash", "cat README.md\nrm -rf ~/notes", PermissionAction.Allow, false)]
    [Arguments("bash", "rm -fr /", PermissionAction.Deny, true)]
    [Arguments("bash", "cd / && rm -rf .", PermissionAction.Deny, true)]
    [Arguments("bash", "/usr/bin/sudo ls", PermissionAction.Deny, true)]
    [Arguments("bash", "rm -rf /", PermissionAction.Deny, true)]
    [Arguments("bash", "sudo ls", PermissionAction.Deny, true)]
    [Arguments("bash", "cat README.md", PermissionAction.Allow, true)]
    [Arguments("write", "src/../../../etc/passwd", PermissionAction.Allow, false)]
    [Arguments("write", "/etc/harbor-redteam-probe.txt", PermissionAction.Allow, false)]
    [Arguments("write", "src/feature/new.ts", PermissionAction.Allow, true)]
    [Arguments("edit", "src/../../secrets.env", PermissionAction.Allow, false)]
    public async Task Evaluate_ReturnsExpectedAction(string tool, string arg, PermissionAction expected, bool isExact)
    {
        var actual = Eval(tool, arg);
        if (isExact)
        {
            await Assert.That(actual).IsEqualTo(expected);
        }
        else
        {
            await Assert.That(actual).IsNotEqualTo(PermissionAction.Allow);
        }
    }
}
