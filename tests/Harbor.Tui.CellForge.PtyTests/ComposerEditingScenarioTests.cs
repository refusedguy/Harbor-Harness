using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — readline-редактирование композера:
///     (a) history recall — Up поднимает прошлый промпт, Down возвращает
///     in-flight draft ровно один раз; (b) kill/yank — Ctrl+U/K убивают до
///     начала/конца строки, Ctrl+Y вставляет последний kill; (c) Enter на
///     пустом композере — no-op без запроса к LLM и без порчи состояния.
/// </summary>
[NotInParallel("pty")]
public sealed class ComposerEditingScenarioTests : CellForgePtyScenarioBase
{
    private static int CountLines(string[] lines, string needle) =>
        lines.Count(x => x.Contains(needle, StringComparison.Ordinal));

    [Test]
    [Timeout(30_000)]
    public async Task HistoryRecall_UpWalksBack_DownRestoresDraft()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        Session.SendKey("HIST-alpha\r");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ok", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Draft typed but NOT submitted — the draft marker never reaches the
        // timeline (it is never submitted), so screen-presence checks isolate
        // the composer without depending on layout row indices.
        Session.SendKey("HIST-draft");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("HIST-draft", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Any(x => x.Contains("HIST-draft", StringComparison.Ordinal))).IsTrue();

        // Up recalls the submitted prompt; the draft is stashed (gone from
        // the screen — the composer now shows the recalled entry instead).
        Session.SendKey("\x1b[A");
        _ = await WaitForScreenAsync(
            l => !l.Any(x => x.Contains("HIST-draft", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Any(x => x.Contains("HIST-draft", StringComparison.Ordinal))).IsFalse();

        // Down restores the captured draft exactly once.
        Session.SendKey("\x1b[B");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("HIST-draft", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Any(x => x.Contains("HIST-draft", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task KillToLineStartAndEnd_ThenYankRestores()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Ctrl+U: kill to line start (caret at end ⇒ everything). The kill
        // text never reached the timeline, so screen absence == buffer empty.
        Session.SendKey("KILLTEXT");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Session.SendKey("\x15"); // Ctrl+U
        _ = await WaitForScreenAsync(
            l => !l.Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal))).IsFalse();

        // Ctrl+Y: the kill comes back.
        Session.SendKey("\x19"); // Ctrl+Y
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal))).IsTrue();

        // Ctrl+K with caret at line start: kill to end — same result.
        Session.SendKey("\x01"); // Ctrl+A → home
        Session.SendKey("\x0b"); // Ctrl+K
        _ = await WaitForScreenAsync(
            l => !l.Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(NormalizedLines().Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal))).IsFalse();

        // Yank again, then submit proves the buffer is functional.
        Session.SendKey("\x19");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("KILLTEXT", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Session.SendKey("\r");
        bool submitted = await Session.WaitForOutputAsync(
            _ => Server.ReceivedRequests.Count > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(submitted).IsTrue();
        await Assert.That(Server.ReceivedRequests[^1].RawBody).Contains("KILLTEXT");
    }

    [Test]
    [Timeout(30_000)]
    public async Task EnterOnEmptyComposer_NoRequest_StateClean()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        Session.SendKey("\r\r\r");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(Server.RequestCount).IsEqualTo(0);

        // No crash, no stray output; a normal submit still works.
        Session.SendKey("after-empty-enters\r");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ok", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(Server.ReceivedRequests[^1].RawBody).Contains("after-empty-enters");
    }
}
