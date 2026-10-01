using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Contract tests for the skill-freshness opt-in (issue #23 slice 2):
///     <see cref="SkillFreshnessPanelRegistration.RegisterSkillFreshness" />
///     appends the panel after the 10 builtins (Alt+1..9 slot order pinned by
///     <see cref="PanelWiringTests" /> stays intact), and the
///     <c>HARBOR_SKILL_FRESHNESS</c> flag gates the CLI wiring.
/// </summary>
/// <remarks>
///     #823: the test below writes the opt-in variable into the process
///     environment and <see cref="SkillFreshnessPanelRegistration" /> reads it back
///     (SkillFreshnessPanelRegistration.cs:44), so the write is process state.
///     Sole writer in this assembly, so the key excludes nobody today; it is here
///     so the next class that reads the flag does not have to rediscover that.
/// </remarks>
[NotInParallel("process-env")]
public class SkillFreshnessPanelRegistrationTests
{
    [Test]
    public async Task RegisterSkillFreshness_AppendsAfterTenBuiltins_PreservingAltSlots()
    {
        var backend = new RecordingBackend();
        using var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance, backend);

        var before = renderer.Panels.Registry.All;
        await Assert.That(before.Count).IsEqualTo(10);

        renderer.Panels.RegisterSkillFreshness(new SkillFreshnessModel());

        var all = renderer.Panels.Registry.All;
        await Assert.That(all.Count).IsEqualTo(11);
        for (int i = 0; i < before.Count; i++)
        {
            await Assert.That(all[i].Id).IsEqualTo(before[i].Id);
        }

        await Assert.That(all[10].Id).IsEqualTo("skill-freshness");
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
