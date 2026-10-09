using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — markdown rendering through the PTY: the mock answers
///     with a header, bold text and a fenced code block; the settled timeline
///     must carry the CONTENT (markup may be styled or stripped — only content
///     words are asserted). Marker asserts only (streaming cadence is
///     nondeterministic — celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class MarkdownRenderScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(60_000)]
    public async Task MarkdownResponse_HeaderBoldCode_ContentLandsOnTimeline()
    {
        Server.SetResponse("test-model",
            "# PTY-заголовок-423\n\nОбычный текст и **жирный-маркер-423** внутри.\n\n```python\nprint(\"pty-code-423\")\n```\n");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("покажи markdown");

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("pty-code-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("pty-code-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("жирный-маркер-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("PTY-заголовок-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
    }
}
