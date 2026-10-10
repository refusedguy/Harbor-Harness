using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — widest/narrowest resize beyond the existing geometry
///     matrix (which covers 100×30→100×12, 100×8 and 20×50 — see
///     <see cref="ResizeGeometryScenarioTests" />): launch wide (160×40), crush
///     to a wide-short frame, squeeze to a narrow-tall frame, prove a turn
///     still lands, then restore. Survival + marker asserts (celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class ExtremeResizeScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(90_000)]
    public async Task WidestThenNarrowest_SurvivesBoth_AndStaysFunctional()
    {
        Server.SetResponse("test-model", "wide-ok-423");
        await StartAppAsync(160, 40).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Widest-short: 160 cols × 10 rows.
        await Session.ResizeAsync(160, 10).ConfigureAwait(false);
        string[] wideShort = await WaitForScreenAsync(
            l => l.All(x => x.Length <= 170)
                && l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(wideShort.All(x => x.Length <= 170)).IsTrue().Because($"screen:\n{ScreenText}");

        // Narrowest-tall: 40 cols × 40 rows (fits the 160-wide emulator buffer).
        await Session.ResizeAsync(40, 40).ConfigureAwait(false);
        string[] narrowTall = await WaitForScreenAsync(
            l => l.All(x => x.Length <= 50)
                && l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(narrowTall.All(x => x.Length <= 50)).IsTrue().Because($"screen:\n{ScreenText}");

        // Still functional at the narrow geometry: a turn runs and lands.
        SubmitLine("narrow-turn");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("wide-ok-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Restore the launch geometry without losing the app.
        await Session.ResizeAsync(160, 40).ConfigureAwait(false);
        string[] restored = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))
                && l.Any(x => x.Contains("wide-ok-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(restored.Any(x => x.Contains("wide-ok-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
    }
}
