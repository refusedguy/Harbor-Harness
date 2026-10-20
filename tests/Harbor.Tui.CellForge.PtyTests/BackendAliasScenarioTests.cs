using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — the legacy alias <c>HARBOR_TUI=consoleex</c> must
///     render identically to the canonical <c>cellforge</c> (the registry maps
///     the alias to the same renderer): boot each backend, settle to a stable
///     idle frame, and compare the normalized grids for equality. The launch
///     idle frame is goldenizable by design (deterministic — mascot and
///     auto-title are off in the harness), so equality here is the shared
///     golden the acceptance criteria ask for.
/// </summary>
public sealed class BackendAliasScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task ConsoleexAlias_RendersSameIdleFrame_AsCellforge()
    {
        string consoleex = await BootAndCaptureSettledFrameAsync("consoleex").ConfigureAwait(false);
        string cellforge = await BootAndCaptureSettledFrameAsync("cellforge").ConfigureAwait(false);

        await Assert.That(consoleex.Contains("Harbor — modular AI coding agent")).IsTrue().Because($"consoleex frame:\n{consoleex}");
        await Assert.That(cellforge.Contains("Harbor — modular AI coding agent")).IsTrue().Because($"cellforge frame:\n{cellforge}");
        await Assert.That(cellforge).IsEqualTo(consoleex).Because($"consoleex frame:\n{consoleex}\n--- cellforge frame:\n{cellforge}");
    }

    private async Task<string> BootAndCaptureSettledFrameAsync(string tui)
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30, tui).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Settle: the frame must STOP CHANGING (no spinner/mascot drift). The
        // old loop compared two snapshots a fixed 400 ms apart and hoped they
        // agreed; the repaint signal answers the same question directly —
        // "did the grid change inside this window?" — and the wait ends the
        // moment the grid is still instead of always paying the full gap.
        var deadline = TimeSpan.FromSeconds(15);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < deadline)
        {
            string first = NormalizeToGoldenText(ScreenText);
            bool repainted = await WaitForRepaintAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            string second = NormalizeToGoldenText(ScreenText);
            if (!repainted || first == second)
            {
                return second;
            }
        }

        throw new TimeoutException($"idle frame never settled for tui={tui}. Screen:\n{ScreenText}");
    }
}
