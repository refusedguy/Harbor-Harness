using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     #2679 F020. <see cref="ConfigPathGuard" /> is a pure classifier leaf. These
///     tests pin the paths an "always allow" decision must never be persisted
///     for, without touching the file system, a config store or an env var.
/// </summary>
public class ConfigPathGuardTests
{
    [Test]
    [Arguments(".harbor/config.json")]
    [Arguments(".harbor/hooks.json")]
    [Arguments("~/.harbor/config.json")]
    [Arguments("/home/nbook/.harbor/config.json")]
    [Arguments("/repo/.harbor/mcp.json")]
    [Arguments(".harbor/plugins/added.cs")]
    [Arguments(".harbor")]
    public async Task HarborSettingsPaths_AreProtected(string path)
    {
        await Assert.That(ConfigPathGuard.IsProtected(path)).IsTrue();
        await Assert.That(ConfigPathGuard.Classify(path)).IsEqualTo(ConfigPathProtection.HarborSettingsDirectory);
    }

    [Test]
    [Arguments("config.json")]
    [Arguments("config.jsonc")]
    [Arguments("tui.json")]
    [Arguments("hooks.json")]
    [Arguments("mcp.json")]
    [Arguments("hosts.json")]
    [Arguments("daemon.psk")]
    [Arguments("./config.json")]
    public async Task BareKnownConfigNames_AreProtected(string path)
    {
        await Assert.That(ConfigPathGuard.IsProtected(path)).IsTrue();
        await Assert.That(ConfigPathGuard.Classify(path)).IsEqualTo(ConfigPathProtection.KnownConfigFileName);
    }

    [Test]
    [Arguments("README.md")]
    [Arguments("src/config.json")]
    [Arguments("config/appsettings.json")]
    [Arguments("src/Harbor.PolicyConfig/Configuration/ConfigStore.cs")]
    [Arguments(".env")]
    [Arguments("harbor.json")]
    [Arguments("config.json.bak")]
    [Arguments("my.harbor/config.json")]
    public async Task OrdinaryAndForeignPaths_AreNotProtected(string path)
    {
        await Assert.That(ConfigPathGuard.IsProtected(path)).IsFalse();
        await Assert.That(ConfigPathGuard.Classify(path)).IsEqualTo(ConfigPathProtection.None);
    }

    [Test]
    public async Task WindowsSeparatorAndCase_AreHandled()
    {
        const string windows = @"C:\Users\nbook\.harbor\config.json";
        await Assert.That(ConfigPathGuard.Classify(windows)).IsEqualTo(ConfigPathProtection.HarborSettingsDirectory);

        await Assert.That(ConfigPathGuard.Classify("  CONFIG.JSON  ")).IsEqualTo(ConfigPathProtection.KnownConfigFileName);
        await Assert.That(ConfigPathGuard.Classify(@".\TUI.json")).IsEqualTo(ConfigPathProtection.KnownConfigFileName);
    }

    [Test]
    public async Task TraversalThroughTheSettingsDirectory_IsStillProtected()
    {
        await Assert.That(ConfigPathGuard.IsProtected("/repo/.harbor/../config.json")).IsTrue();
    }

    [Test]
    public async Task NullEmptyAndWhitespace_AreNotProtected()
    {
        await Assert.That(ConfigPathGuard.IsProtected(null)).IsFalse();
        await Assert.That(ConfigPathGuard.Classify(null)).IsEqualTo(ConfigPathProtection.None);
        await Assert.That(ConfigPathGuard.Classify("")).IsEqualTo(ConfigPathProtection.None);
        await Assert.That(ConfigPathGuard.Classify("   ")).IsEqualTo(ConfigPathProtection.None);
    }

    [Test]
    public async Task CanPersistAlways_IsTheExactInverse()
    {
        string[] samples =
        [
            ".harbor/config.json",
            "config.json",
            "./tui.json",
            "src/config.json",
            "README.md",
            "",
        ];

        foreach (string path in samples)
        {
            await Assert.That(ConfigPathGuard.CanPersistAlways(path)).IsEqualTo(!ConfigPathGuard.IsProtected(path));
        }
    }
}
