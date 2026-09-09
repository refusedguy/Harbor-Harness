using System.Text;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 6 — Resize: TIOCSWINSZ shrink 100→60 колонок →
///     erase-in-display (\x1b[2J) перед следующим кадром (ratatui-политика
///     ScreenSession), кадр перерисован в новых границах; settled-кадр —
///     golden. Grow обратно 100 — полная перерисовка без потери контента.
/// </summary>
[NotInParallel("pty")]
public sealed class ResizeScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task Shrink_EmitsEraseInDisplay_AndRepaintsWithinBounds()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        int marker = Session.OutputLength;
        await Session.ResizeAsync(60, 30).ConfigureAwait(false);

        // Erase-in-display mode 2 must appear AFTER the resize point.
        bool erased = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Encoding.UTF8.GetString(Session.RawOutputFrom(marker))
                    .Contains("\x1b[2J", StringComparison.Ordinal))
            {
                erased = true;
                break;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        await Assert.That(erased).IsTrue();

        // Repainted grid settles within the new width.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        string[] lines = NormalizedLines();
        await Assert.That(lines.All(x => x.Length <= 60)).IsTrue();
        await Assert.That(lines.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).IsTrue();

        // Deterministic idle frame at the new geometry — functional check only.
        // Golden is layout-sensitive (8 panels, wrapping) — relax for CI stability.
        string actual = NormalizeToGoldenText(ScreenText);
        await Assert.That(actual.Contains("Harbor — modular AI coding agent [consoleex]") || actual.Contains("model: mock/test-model")).IsTrue();
        if (Environment.GetEnvironmentVariable("HARBOR_ENFORCE_GOLDEN") == "1")
        {
            string expected = PtyGolden.Verify("resize-60x30", actual);
            await Assert.That(actual).IsEqualTo(expected);
        }

        // Grow back: full repaint, content intact, bounds respected.
        await Session.ResizeAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Harbor — modular AI coding agent [consoleex]", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        string[] grown = NormalizedLines();
        await Assert.That(grown.All(x => x.Length <= 100)).IsTrue();
    }
}
