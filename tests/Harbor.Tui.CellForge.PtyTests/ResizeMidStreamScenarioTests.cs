using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — Resize mid-stream: TIOCSWINSZ shrink
///     100→76 КОЛОНКАМИ ВО ВРЕМЯ активного стрима ответа. Контракт: стрим
///     доигрывает до конца, полный текст ответа присутствует на эмулированной
///     сетке после settle, ни одна строка не выходит за новую ширину,
///     статус возвращается в idle (ход не потерян и не «завис»).
/// </summary>
[NotInParallel("pty")]
public sealed class ResizeMidStreamScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(45_000)]
    public async Task Resize_MidStream_CompletesWithinNewBounds()
    {
        // ~900 chars at the mock's 4-chars/50ms cadence ≈ 10 s of streaming —
        // enough headroom to resize in the middle of the run.
        Server.SetResponse("test-model", "RESIZE-" + new string('m', 780) + "-DONE");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("stream-and-shrink");
        bool running = await Session.WaitForOutputAsync(
            text => text.Contains("ход", StringComparison.Ordinal) || text.Contains("…", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(running).IsTrue();

        // Shrink WHILE the turn is streaming.
        await Session.ResizeAsync(76, 30).ConfigureAwait(false);

        // The full response still lands on the emulated grid.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("DONE", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("DONE", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        string[] settled = NormalizedLines();

        // No line exceeds the new width; the streamed payload survived intact.
        await Assert.That(settled.All(x => x.Length <= 76)).IsTrue();
        await Assert.That(settled.Any(x => x.Contains("RESIZE-", StringComparison.Ordinal))).IsTrue();
        await Assert.That(settled.Any(x => x.Contains("DONE", StringComparison.Ordinal))).IsTrue();

        // Turn finished: the app returned to idle (prompt accepts new input).
        Server.SetResponse("test-model", "AFTER-RESIZE-OK");
        SubmitLine("after");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("AFTER-RESIZE-OK", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }
}
