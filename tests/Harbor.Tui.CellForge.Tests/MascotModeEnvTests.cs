using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// HARBOR_MASCOT_MODE resolver (mascot-brand T2): HARBOR_MASCOT=off stays the
/// hard kill-switch; panel/footer/off parse case-insensitively; anything else
/// falls back to the footer default.
/// </summary>
public class MascotModeEnvTests
{
    [Test]
    public async Task HARBOR_MASCOT_Off_Wins_Over_Anything()
    {
        await Assert.That(MascotModeEnv.Resolve("off", "panel")).IsEqualTo(MascotMode.Off);
        await Assert.That(MascotModeEnv.Resolve("OFF", "footer")).IsEqualTo(MascotMode.Off);
        await Assert.That(MascotModeEnv.Resolve("off", null)).IsEqualTo(MascotMode.Off);
    }

    [Test]
    public async Task HARBOR_MASCOT_MODE_Parses_All_Three_Values()
    {
        await Assert.That(MascotModeEnv.Resolve(null, "panel")).IsEqualTo(MascotMode.Panel);
        await Assert.That(MascotModeEnv.Resolve(null, "PANEL")).IsEqualTo(MascotMode.Panel);
        await Assert.That(MascotModeEnv.Resolve(null, " panel ")).IsEqualTo(MascotMode.Panel);
        await Assert.That(MascotModeEnv.Resolve(null, "off")).IsEqualTo(MascotMode.Off);
        await Assert.That(MascotModeEnv.Resolve(null, "footer")).IsEqualTo(MascotMode.Footer);
    }

    [Test]
    public async Task Unknown_Or_Missing_Values_Default_To_Footer()
    {
        await Assert.That(MascotModeEnv.Resolve(null, null)).IsEqualTo(MascotMode.Footer);
        await Assert.That(MascotModeEnv.Resolve(null, "bogus")).IsEqualTo(MascotMode.Footer);
        await Assert.That(MascotModeEnv.Resolve("1", "panel")).IsEqualTo(MascotMode.Panel);
        await Assert.That(MascotModeEnv.Resolve("", "panel")).IsEqualTo(MascotMode.Panel);
    }
}
