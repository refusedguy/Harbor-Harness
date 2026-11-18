using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     F019: a broad path Allow (the default <c>read: * = Allow</c>) must not silently
///     disclose dotenv files. The guard lives in the path safety policy, so it also
///     applies to any future path-taking tool that declares a Path safety profile.
/// </summary>
public class SensitivePathGuardTests
{
    [Test]
    [Arguments(".env")]
    [Arguments(".env.local")]
    [Arguments(".env.production")]
    [Arguments("config/.env")]
    [Arguments("config/.env.local")]
    [Arguments("src\\.env")]
    public async Task BroadReadAllow_DotenvPaths_AreAsked(string path)
    {
        var action = PermissionRuleset.Default.Evaluate("read", path);
        await Assert.That(action).IsEqualTo(PermissionAction.Ask)
            .Because($"'{path}' is a dotenv file a wildcard read Allow must not disclose");
    }

    [Test]
    [Arguments(".env.example")]
    [Arguments("config/.env.example")]
    [Arguments("README.md")]
    public async Task BroadReadAllow_TemplateAndOrdinaryPaths_StayAllowed(string path)
    {
        var action = PermissionRuleset.Default.Evaluate("read", path);
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task ExplicitLiteralAllow_IsStillHonoured()
    {
        var ruleset = new PermissionRuleset([new PermissionRule("read", ".env", PermissionAction.Allow)]);
        var action = ruleset.Evaluate("read", ".env");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow)
            .Because("an exact literal rule can only match that one file, so it is explicit intent");
    }

    [Test]
    public async Task ExplicitDeny_StillWinsOverTheGuard()
    {
        var action = PermissionRuleset.Default.Evaluate("edit", ".env");
        await Assert.That(action).IsEqualTo(PermissionAction.Deny);
    }
}
