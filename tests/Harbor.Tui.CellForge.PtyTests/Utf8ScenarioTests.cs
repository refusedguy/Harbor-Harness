using System.Text.Json;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — UTF-8 через PTY: мультибайтовые
///     последовательности (кириллица, CJK wide-char, emoji) дробятся
///     разбиением writes'ов посреди кодовой единицы; incremental decoder
///     восстанавливает руны, текст виден на сетке и доезжает до LLM
///     побайтно-точно (JSON-escape на проводе).
/// </summary>
[NotInParallel("pty")]
public sealed class Utf8ScenarioTests : CellForgePtyScenarioBase
{
    private const string MixedText = "привет 你好 🚀";

    [Test]
    [Timeout(30_000)]
    public async Task MixedWidthUtf8_SplitAcrossWrites_RoundTripsToModel()
    {
        Server.SetResponse("test-model", "UTF8-OK");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Byte-level send split mid-codepoint (the 🚀 emoji is 4 UTF-8 bytes).
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(MixedText + "\r");
        Session.Write(bytes[..7]);
        await Task.Delay(20).ConfigureAwait(false);
        Session.Write(bytes[7..13]);
        await Task.Delay(20).ConfigureAwait(false);
        Session.Write(bytes[13..]);

        // The user block renders the mixed-width text on the grid.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("привет", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Task.Delay(400).ConfigureAwait(false);

        string[] lines = NormalizedLines();
        await Assert.That(lines.Any(x => x.Contains("你好", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines.Any(x => x.Contains("🚀", StringComparison.Ordinal))).IsTrue();

        // Exact content reached the model (non-ASCII is \uXXXX-escaped on wire).
        bool submitted = await Session.WaitForOutputAsync(
            _ => Server.RequestCount > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(submitted).IsTrue();
        await Assert.That(LastUserContent(Server.ReceivedRequests[^1].RawBody)).IsEqualTo(MixedText);
    }

    /// <summary>Last user-message content of a chat-completions body (JSON-aware).</summary>
    private static string LastUserContent(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
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
