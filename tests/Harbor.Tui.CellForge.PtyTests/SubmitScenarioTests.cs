using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 2 — Submit: «привет» + Enter → mock-ответ стримится
///     построчно → блок коммитится и остаётся на экране, статус возвращается
///     в idle. Golden-байты здесь сознательно НЕ сверяются: cadence стрима
///     (4 символа / 50 мс) делает промежуточные кадры недетерминированными —
///     вместо этого маркерные ассерты финального состояния (celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class SubmitScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task Submit_UserBlock_MockResponseStreams_AndCommits()
    {
        Server.SetResponse("test-model", "Привет из mock!");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("привет");

        // Response MUST be asserted on the emulated GRID, not raw bytes: the
        // renderer streams timeline deltas as cursor-positioned runs
        // ("Привет" … CUP … "из" … CUP … "mock!"), so the contiguous phrase
        // never exists in the master-byte stream (celldiff §8 contract).
        string[] lines = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Привет из mock!", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        _ = lines;

        var requests = Server.ReceivedRequests;
        await Assert.That(requests.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(LastUserContent(requests[^1].RawBody)).IsEqualTo("привет");

        // Block COMMITTED (survives past streaming end), composer cleared.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("Привет из mock!", StringComparison.Ordinal))).IsTrue();
        await Assert.That(settled.Any(x => x.Contains("привет", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Last user-message content of a chat-completions body. JSON-aware:
    /// non-ASCII text is \uXXXX-escaped on the wire, so raw Contains() cannot match.</summary>
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
