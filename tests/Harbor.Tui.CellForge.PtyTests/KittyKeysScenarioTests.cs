using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 3 — Kitty keys: CSI-u Shift+Enter (\x1b[13;2u)
///     вставляет newline в композер, НЕ отправляет промпт; обычный \r потом
///     уходит одним сообщением, содержащим обе строки.
/// </summary>
[NotInParallel("pty")]
public sealed class KittyKeysScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task KittyShiftEnter_InsertsNewline_AndDoesNotSubmit()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        Session.SendKey("AA");
        Session.SendKey("\x1b[13;2u"); // kitty CSI u: Shift+Enter
        Session.SendKey("BB");

        await Task.Delay(400).ConfigureAwait(false);

        // THE assertion: newline inserted, nothing submitted yet.
        await Assert.That(Server.RequestCount).IsEqualTo(0);

        // Composer shows both lines in order (AA above BB).
        string[] lines = NormalizedLines();
        int aa = Array.FindIndex(lines, x => x.Contains("AA", StringComparison.Ordinal));
        int bb = Array.FindIndex(lines, x => x.Contains("BB", StringComparison.Ordinal));
        await Assert.That(aa).IsGreaterThanOrEqualTo(0);
        await Assert.That(bb).IsGreaterThan(aa);

        // Plain Enter now submits ONE message carrying the embedded newline.
        // Signal on recorded chat-completions, not RequestCount: the lazy
        // /models fetch bumps the total without adding to ReceivedRequests,
        // which races the [^1] index below.
        Session.SendKey("\r");
        bool submitted = await Session.WaitForOutputAsync(
            _ => Server.ReceivedRequests.Count > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(submitted).IsTrue();

        var body = Server.ReceivedRequests[^1].RawBody;
        await Assert.That(body).Contains("AA\\nBB");
    }
}
