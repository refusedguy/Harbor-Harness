using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — bracketed-paste edges: (a) paste с
///     встроенными переводами строк вставляется ВЕРБАТИМ одним блоком —
///     ни одна строка payload'а не выполняется как отдельная команда;
///     (b) большой paste (2.5 KB, дробится ядром PTY на куски) целиком
///     попадает в композер — watchdog-границы чанков не теряют текст.
/// </summary>
[NotInParallel("pty")]
public sealed class PasteEdgeScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task MultilinePaste_InsertsVerbatim_NeverSubmitsPerLine()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Embedded newlines: a naive per-line submitter would fire each line
        // as a separate turn. The paste contract keeps it verbatim. (A payload
        // with a leading slash is covered by PasteInjectionScenarioTests.)
        Session.SendKey("\x1b[200~alpha\nbravo\n\x1b[201~");
        await Task.Delay(700).ConfigureAwait(false);

        // Nothing executed while pasting: zero LLM requests.
        await Assert.That(Server.RequestCount).IsEqualTo(0);

        // Both payload fragments visible in the composer area.
        string[] lines = NormalizedLines();
        await Assert.That(lines.Any(x => x.Contains("alpha", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines.Any(x => x.Contains("bravo", StringComparison.Ordinal))).IsTrue();

        // Plain Enter submits ONE message with the embedded newline intact.
        Session.SendKey("\r");
        bool submitted = await Session.WaitForOutputAsync(
            _ => Server.RequestCount > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(submitted).IsTrue();

        var body = Server.ReceivedRequests[^1].RawBody;
        await Assert.That(body).Contains("alpha\\nbravo");
    }

    [Test]
    [Timeout(45_000)]
    public async Task LargePasteAcrossChunkBoundaries_LandsVerbatim()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // 2.5 KB single-line payload; PTY writes it in 512-byte chunks with
        // gaps — parser chunk boundaries + paste watchdog must not lose a
        // byte. The grid composer clips at the row width, so the HEAD is
        // asserted on screen and the FULL content on the wire after submit.
        string payload = string.Concat(Enumerable.Range(0, 100).Select(i => $"chunk{i:000} "));
        string paste = "\x1b[200~" + payload + "\x1b[201~";
        for (int off = 0; off < paste.Length; off += 512)
        {
            Session.SendKey(paste[off..Math.Min(off + 512, paste.Length)]);
            await Task.Delay(20).ConfigureAwait(false);
        }

        // Head of the payload visible in the clipped composer row.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("chunk000", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);

        // Nothing executed while pasting.
        await Assert.That(Server.RequestCount).IsEqualTo(0);

        // Submit → the FULL 2.5 KB reaches the model byte-exact.
        Session.SendKey("\r");
        bool submitted = await Session.WaitForOutputAsync(
            _ => Server.RequestCount > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(submitted).IsTrue();

        string received = LastUserContent(Server.ReceivedRequests[^1].RawBody);
        await Assert.That(received).IsEqualTo(payload.TrimEnd());
    }

    /// <summary>Last user-message content of a chat-completions body (JSON-aware).</summary>
    private static string LastUserContent(string rawBody)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
        foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray().Reverse())
        {
            if (msg.GetProperty("role").GetString() == "user")
            {
                return msg.GetProperty("content").GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
