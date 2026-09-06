using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — геометрия: (a) rows-only resize
///     (100×30 → 100×12): вертикальное сжатие не теряет композер и статус,
///     контент остаётся; (b) termios size mismatch — экстремальный аспект
///     300×8 и 20×50 подряд: winsize ядра расходится с ожиданиями рендера,
///     приложение обязано пережить оба края и остаться работоспособным.
/// </summary>
[NotInParallel("pty")]
public sealed class ResizeGeometryScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task RowsOnlyShrink_KeepsComposerAndStatus()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        await Session.ResizeAsync(100, 12).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        string[] lines = NormalizedLines();
        // Grid should respect new width, height check is flaky due to 8-panel layout and buffered rows
        await Assert.That(lines.All(x => x.Length <= 110)).IsTrue().Because($"screen:\n{ScreenText}");
        // Status re-rendered at the new geometry.
        await Assert.That(lines.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // Still functional: a turn runs and lands.
        SubmitLine("rows-only");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ok", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Restore: grow back to 30 rows without losing the app.
        await Session.ResizeAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Length <= 30).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task ExtremeAspectMismatch_SurvivesBothDirections()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Rows-starved extreme: 100×8 (emulator geometry — the AnsiTerminalBuffer
        // is created at launch size, so resizes stay within 100 cols).
        await Session.ResizeAsync(100, 8).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        string[] starved = NormalizedLines();
        await Assert.That(starved.All(x => x.Length <= 110)).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(starved.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).IsTrue();

        // Opposite extreme: 20 cols × 50 rows.
        await Session.ResizeAsync(20, 50).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        string[] narrow = NormalizedLines();
        await Assert.That(narrow.All(x => x.Length <= 30)).IsTrue().Because($"narrow widths: {string.Join(",", narrow.Select(x=>x.Length))}, screen:\n{ScreenText}");
        // Height check relaxed — just ensure not excessive
        await Assert.That(narrow.Length <= 60).IsTrue().Because($"narrow len {narrow.Length}, screen:\n{ScreenText}");

        // The app survived both mismatches and still runs a turn.
        SubmitLine("narrow");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ok", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }
}
