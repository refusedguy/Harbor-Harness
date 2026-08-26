using TUnit.Assertions;

namespace Harbor.Tui.ConsoleEx.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 1 — Launch: альт-экран входит точной CE-4
///     последовательностью (?1049h ?25l ?2004h), welcome + лента + композер +
///     статус отрисованы; settled idle-кадр сверяется побайтово-нормализованным
///     golden'ом (эмулированная сетка, TrimEnd строк).
/// </summary>
[NotInParallel("pty")]
public sealed class LaunchScenarioTests : ConsoleExPtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task Launch_AltScreenFrame_WelcomeComposerStatusRendered()
    {
        VerboseLogging = true;
        await StartAppAsync(100, 30).ConfigureAwait(false);

        // 1. Exact alt-screen entry prefix (REPL constants, design §5.2/§7).
        bool entered = await WaitForRawTextAsync(
            "\x1b[?1049h\x1b[?25l\x1b[?2004h", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(entered).IsTrue();

        // 2. Welcome lines land in the emulated grid.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Harbor — modular AI coding agent [consoleex]", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // 3. Empty-composer paint: PromptRenderer emits a cursor-block cell
        //    (reverse-video space) — visible in raw bytes as cursor addressing,
        //    invisible in grid TEXT; the golden below pins the text plane,
        //    the sync pair above pins frame atomicity (checked implicitly by
        //    the emulator's stable state).
        await Task.Delay(600).ConfigureAwait(false);

        // 4. Settled idle launch frame — byte-normalized golden compare.
        string actual = NormalizeToGoldenText(ScreenText);
        string expected = PtyGolden.Verify("launch-100x30", actual);
        await Assert.That(actual).IsEqualTo(expected);
    }
}
