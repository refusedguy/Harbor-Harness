using System.Text;
using Harbor.E2E.Framework;

namespace Harbor.E2E.Cli;

/// <summary>
///     Recording-replay contract of <see cref="MockLlmServer" />: what was
///     served under recording is served identically — and in the same order —
///     by a fresh server in replay mode, including tool-call turns and error
///     entries, with a loud marker once the recorded sequence is exhausted.
/// </summary>
[ParallelLimiter<MockServerLimit>]
public class RecordingReplayTests : IAsyncDisposable
{
    private readonly List<MockLlmServer> _servers = [];

    [Test]
    public async Task Recorded_Sequence_Replays_Identically_In_Order()
    {
        string recordingPath = Path.Combine(Path.GetTempPath(), $"harbor-rec-{Guid.NewGuid():N}.jsonl");
        try
        {
            // ── Record: two text turns + one tool call + one error ──
            var recorder = CreateServer();
            await recorder.StartAsync();
            recorder.StartRecording(recordingPath);
            recorder.SetResponse("m-a", "hello replay");
            recorder.SetResponse("m-b", "second model");
            recorder.SetToolCallResponse("m-tool", "read", new { path = "x.cs", limit = 10 });
            recorder.SetErrorResponse("m-err", "boom");

            HttpClient client = new();
            string firstTurn = await PostCompletionAsync(client, recorder.BaseUri, "m-a");
            string toolTurn = await PostCompletionAsync(client, recorder.BaseUri, "m-tool");
            string errRaw500 = await PostRawExpect500Async(client, recorder.BaseUri, "m-err");
            string secondModel = await PostCompletionAsync(client, recorder.BaseUri, "m-b");

            await Assert.That(ExtractContent(firstTurn)).Contains("hello replay");
            await Assert.That(toolTurn).Contains("\"read\"");
            await Assert.That(errRaw500).Contains("boom");
            await Assert.That(ExtractContent(secondModel)).Contains("second model");
            await recorder.StopAsync();

            // ── Replay: no scripted responses at all, purely the recording ──
            var replayer = CreateServer();
            await replayer.StartAsync();
            replayer.ReplayFrom(recordingPath);

            string replayFirst = await PostCompletionAsync(client, replayer.BaseUri, "m-a");
            string replayTool = await PostCompletionAsync(client, replayer.BaseUri, "m-tool");
            _ = await PostRawExpect500Async(client, replayer.BaseUri, "m-err");
            string replaySecond = await PostCompletionAsync(client, replayer.BaseUri, "m-b");

            await Assert.That(ExtractContent(replayFirst)).IsEqualTo(ExtractContent(firstTurn));
            await Assert.That(replayTool).Contains("\"read\"");
            await Assert.That(ExtractContent(replaySecond)).Contains("second model");

            // Exhaustion is loud, not silent repetition.
            string exhausted = await PostCompletionAsync(client, replayer.BaseUri, "m-a");
            await Assert.That(ExtractContent(exhausted)).Contains("recording exhausted for model 'm-a'");
        }
        finally
        {
            File.Delete(recordingPath);
        }
    }

    [Test]
    public async Task Recording_Without_Entries_Replays_As_Exhausted()
    {
        string recordingPath = Path.Combine(Path.GetTempPath(), $"harbor-rec-{Guid.NewGuid():N}.jsonl");
        try
        {
            var server = CreateServer();
            await server.StartAsync();
            server.StartRecording(recordingPath);

            HttpClient client = new();
            _ = await PostCompletionAsync(client, server.BaseUri, "never-configured");

            // Same live instance switches to replay; StartAsync is once-only.
            server.ReplayFrom(recordingPath);
            string replayed = await PostCompletionAsync(client, server.BaseUri, "never-configured");

            // The fallback marker served during recording becomes the replayed content.
            await Assert.That(ExtractContent(replayed)).Contains("no mock response configured for model 'never-configured'");
        }
        finally
        {
            File.Delete(recordingPath);
        }
    }

    private MockLlmServer CreateServer()
    {
        var server = new MockLlmServer();
        _servers.Add(server);
        return server;
    }

    /// <summary>Concatenate all delta.content values from an SSE chat-completion body.</summary>
    private static string ExtractContent(string sseBody)
    {
        var sb = new StringBuilder();
        foreach (var line in sseBody.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line.Contains("[DONE]"))
                continue;

            using var doc = System.Text.Json.JsonDocument.Parse(line["data: ".Length..]);
            foreach (var choice in doc.RootElement.GetProperty("choices").EnumerateArray())
            {
                if (choice.GetProperty("delta").TryGetProperty("content", out var content)
                    && content.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    sb.Append(content.GetString());
                }
            }
        }

        return sb.ToString();
    }

    private static async Task<string> PostCompletionAsync(HttpClient client, Uri baseUri, string model)
    {
        var body = new { model, messages = new[] { new { role = "user", content = "hi" } } };
        using var response = await client.PostAsync(
            new Uri(baseUri, "/chat/completions"),
            new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> PostRawExpect500Async(HttpClient client, Uri baseUri, string model)
    {
        var body = new { model, messages = new[] { new { role = "user", content = "hi" } } };
        using var response = await client.PostAsync(
            new Uri(baseUri, "/chat/completions"),
            new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        await Assert.That((int)response.StatusCode).IsEqualTo(500);
        return await response.Content.ReadAsStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
        {
            try { await server.StopAsync().ConfigureAwait(false); }
            catch { /* dispose-best-effort */ }

            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
