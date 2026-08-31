using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — kitty CSI-u edge cases: модификаторные
///     биты CellForge-контракта — shift=1, ctrl=2, alt=4 (§2.3, НЕ
///     kitty-стандарт): (a) Alt+Enter (13;5u) вставляет newline (как
///     Shift+Enter), Ctrl+Enter (13;3u) игнорируется целиком (ни submit, ни
///     newline); (b) неизвестный CSI-u (999u) и мусорные последовательности
///     глотаются парсером без влияния на композер и без падения.
/// </summary>
[NotInParallel("pty")]
public sealed class KittyEdgeScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task ModifierEnter_AltInsertsNewline_CtrlIgnored()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        Session.SendKey("AA");
        Session.SendKey("\x1b[13;5u"); // Alt+Enter (alt bit = 4) → newline
        Session.SendKey("BB");

        await Task.Delay(400).ConfigureAwait(false);
        await Assert.That(Server.RequestCount).IsEqualTo(0);
        string[] lines = NormalizedLines();
        int aa = Array.FindIndex(lines, x => x.Contains("AA", StringComparison.Ordinal));
        int bb = Array.FindIndex(lines, x => x.Contains("BB", StringComparison.Ordinal));
        await Assert.That(aa).IsGreaterThanOrEqualTo(0);
        await Assert.That(bb).IsGreaterThan(aa);

        // Ctrl+Enter (ctrl bit = 2): neither submit nor newline — the buffer
        // keeps both lines. Signal on recorded chat-completions, not
        // RequestCount: the registry's lazy /models fetch bumps the total
        // request count without adding to ReceivedRequests, which races the
        // [^1] index below (ArgumentOutOfRangeException on an empty snapshot).
        int before = Server.ReceivedRequests.Count;
        Session.SendKey("\x1b[13;3u");
        await Task.Delay(400).ConfigureAwait(false);
        await Assert.That(Server.ReceivedRequests.Count).IsEqualTo(before);

        // Plain Enter submits the accumulated draft as one message.
        Session.SendKey("\r");
        bool submitted = await Session.WaitForOutputAsync(
            _ => Server.ReceivedRequests.Count > before, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(submitted).IsTrue();
        await Assert.That(Server.ReceivedRequests[^1].RawBody).Contains("AA\\nBB");
    }

    [Test]
    [Timeout(30_000)]
    public async Task UnknownCsiU_AndGarbage_IgnoredComposerUnaffected()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Unknown CSI-u codepoint, broken CSI (no final), SS3 stray — all noise.
        Session.SendKey("\x1b[999u");
        Session.SendKey("\x1b[<u");
        Session.SendKey("\x1bOZ");
        await Task.Delay(400).ConfigureAwait(false);

        // App alive, nothing executed, composer still accepts input.
        await Assert.That(Server.RequestCount).IsEqualTo(0);
        Session.SendKey("alive\r");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ok", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var body = Server.ReceivedRequests[^1].RawBody;
        await Assert.That(body).Contains("alive");
    }
}
