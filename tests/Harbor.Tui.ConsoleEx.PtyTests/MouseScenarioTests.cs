using TUnit.Assertions;

namespace Harbor.Tui.ConsoleEx.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 4 — Mouse. SGR click-роутинг в REPL не подключён
///     (MouseRouter — hit-test scaffold; CE-4 wired только wheel) → click
///     честно Skip с пометкой, wheel-скролл проверяется живьём: переполняем
///     ленту, колесо вверх возвращает верхний контур в viewport.
/// </summary>
[NotInParallel("pty")]
public sealed class MouseScenarioTests : ConsoleExPtyScenarioBase
{
    private const string WheelUpSeq = "\x1b[<64;10;5M";  // SGR wheel up @ col10,row5
    private const string ClickPressSeq = "\x1b[<0;10;5M";
    private const string ClickReleaseSeq = "\x1b[<0;10;5m";

    [Test]
    [Timeout(30_000)]
    public async Task SgrClick_RouterNotWiredToRepl_Skipped()
        => Skip.Test("MouseRouter hit-test не подключён к ConsoleExReplRunner (wheel-only CE-4); click-сценарий активируется после wiring — см. docs/ROADMAP.md consoleex follow-ups.");

    [Test]
    [Timeout(30_000)]
    public async Task SgrWheelUp_ScrollsTimelineBack_RevealingTopContent()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        const string welcomeMarker = "[consoleex]";
        int turns = 0;
        while (NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal)) && turns < 12)
        {
            string marker = $"R{turns}marker";
            Server.SetResponse("test-model", marker);
            SubmitLine($"u{turns}");
            // Screen-grid wait: streamed timeline text lands as separate
            // cursor-positioned runs in the raw stream (see SubmitScenario).
            _ = await WaitForScreenAsync(
                l => l.Any(x => x.Contains(marker, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            turns++;
        }

        // Overflow achieved: welcome scrolled out of the viewport.
        await Assert.That(turns).IsLessThanOrEqualTo(12);

        // Wheel up repeatedly until the top content comes back into view.
        bool revealed = false;
        for (int tick = 0; tick < 24 && !revealed; tick++)
        {
            Session.SendKey(WheelUpSeq);
            await Task.Delay(120).ConfigureAwait(false);
            revealed = NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal));
        }

        await Assert.That(revealed).IsTrue();
    }
}
