using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Contract tests for the skill-freshness opt-in (issue #23 slice 2):
///     <see cref="SkillFreshnessPanelRegistration.RegisterSkillFreshness" />
///     appends the panel after the 9 builtins (Alt+1..9 slot order pinned by
///     <see cref="PanelWiringTests" /> stays intact), and the
///     <c>HARBOR_SKILL_FRESHNESS</c> flag gates the CLI wiring.
/// </summary>
public class SkillFreshnessPanelRegistrationTests
{
    [Test]
    public async Task RegisterSkillFreshness_AppendsAfterNineBuiltins_PreservingAltSlots()
    {
        var backend = new RecordingBackend();
        using var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance, backend);

        var before = renderer.Panels.Registry.All;
        await Assert.That(before.Count).IsEqualTo(9);

        renderer.Panels.RegisterSkillFreshness(new SkillFreshnessModel());

        var all = renderer.Panels.Registry.All;
        await Assert.That(all.Count).IsEqualTo(10);
        for (int i = 0; i < before.Count; i++)
        {
            await Assert.That(all[i].Id).IsEqualTo(before[i].Id);
        }

        await Assert.That(all[9].Id).IsEqualTo("skill-freshness");
    }

    [Test]
    public async Task OptIn_Flag_OffByDefault_OnWhenSet()
    {
        string? previous = Environment.GetEnvironmentVariable(SkillFreshnessPanelRegistration.OptInVariable);
        try
        {
            Environment.SetEnvironmentVariable(SkillFreshnessPanelRegistration.OptInVariable, null);
            await Assert.That(SkillFreshnessPanelRegistration.SkillFreshnessOptInEnabled()).IsFalse();

            Environment.SetEnvironmentVariable(SkillFreshnessPanelRegistration.OptInVariable, "1");
            await Assert.That(SkillFreshnessPanelRegistration.SkillFreshnessOptInEnabled()).IsTrue();

            Environment.SetEnvironmentVariable(SkillFreshnessPanelRegistration.OptInVariable, "true");
            await Assert.That(SkillFreshnessPanelRegistration.SkillFreshnessOptInEnabled()).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SkillFreshnessPanelRegistration.OptInVariable, previous);
        }
    }
}
