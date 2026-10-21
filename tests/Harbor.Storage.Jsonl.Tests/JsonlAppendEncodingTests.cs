using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;
namespace Harbor.Storage.Jsonl.Tests;
/// <summary>
///     Byte-format tests for the #177 append path: lines must be source-gen
///     UTF-8 bytes plus a single LF terminator (no <c>Serialize + "\n"</c>
///     string concat, no CR), and the update rewrite must keep the same
///     line-wise encoding with exactly one entry per message id.
/// </summary>
public class JsonlAppendEncodingTests
{
    private static JsonlSessionStore CreateStore(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    private static string SingleSessionFile(string tempDir) =>
        Directory.GetFiles(tempDir, "*.jsonl") is { Length: 1 } files
            ? files[0]
            : throw new InvalidOperationException("Expected exactly one session file.");

    [Test]
    public async Task AppendMessageAsync_WritesSingleLfTerminatedLine()
    {
        var store = CreateStore(out string tempDir);
        try
        {
            var session = (await store.CreateAsync("/test", "code", "p", "p/m")).Value;
            var message = TestMessages.User("hi", id: "m1", sessionId: session.Id, model: "p/m");
            await store.AppendMessageAsync(session.Id, message);

            byte[] raw = await File.ReadAllBytesAsync(SingleSessionFile(tempDir));

            await Assert.That(raw.Contains((byte)'\r')).IsFalse();
            await Assert.That(raw[^1]).IsEqualTo((byte)'\n');
            string text = Encoding.UTF8.GetString(raw);
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Length).IsEqualTo(2);
            await Assert.That(lines[1].Contains("\"type\":\"message\"")).IsTrue();
            await Assert.That(lines[1].Contains("\"id\":\"m1\"")).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task AppendMessageAsync_BytesMatchSourceGenEncoding()
    {
        var store = CreateStore(out string tempDir);
        try
        {
            var session = (await store.CreateAsync("/test", "code", "p", "p/m")).Value;
            var message = TestMessages.User("hi", id: "m1", sessionId: session.Id, model: "p/m");
            await store.AppendMessageAsync(session.Id, message);

            byte[] raw = await File.ReadAllBytesAsync(SingleSessionFile(tempDir));
            string text = Encoding.UTF8.GetString(raw);
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var expectedEntry = new MessageEntry(
                "message",
                message.Id,
                message.ParentId,
                message.Role,
                message.CreatedAt,
                JsonlMessageCodec.SerializeMessagePayload(message));
            byte[] expected = JsonSerializer.SerializeToUtf8Bytes(expectedEntry, JsonlCodecContext.Default.MessageEntry);

            await Assert.That(lines[1]).IsEqualTo(Encoding.UTF8.GetString(expected));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    ///     The edit is APPENDED (#1752), so the file holds both versions of the
    ///     message — and the encoding of the appended one is the same LF-only
    ///     shape the append path writes.
    /// </summary>
    [Test]
    public async Task UpdateMessageAsync_KeepsLfOnlyEncodingAndAppendsTheVersion()
    {
        var store = CreateStore(out string tempDir);
        try
        {
            var session = (await store.CreateAsync("/test", "code", "p", "p/m")).Value;
            var message = TestMessages.User("v1", id: "m1", sessionId: session.Id, model: "p/m");
            await store.AppendMessageAsync(session.Id, message);
            await store.UpdateMessageAsync(session.Id, message with { Content = "v2" });

            byte[] raw = await File.ReadAllBytesAsync(SingleSessionFile(tempDir));

            await Assert.That(raw.Contains((byte)'\r')).IsFalse();
            string text = Encoding.UTF8.GetString(raw);
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // The header, the version that was there, and the version that
            // replaced it — the record is immutable, so the earlier one stays.
            await Assert.That(lines.Length).IsEqualTo(3);
            int idHits = 0;
            foreach (string line in lines)
            {
                if (line.Contains("\"id\":\"m1\"", StringComparison.Ordinal))
                    idHits++;
            }
            await Assert.That(idHits).IsEqualTo(2);
            await Assert.That(lines[1].Contains("v1")).IsTrue();
            await Assert.That(lines[2].Contains("v2")).IsTrue();

            // And the surface shows the current version — one message, not two.
            var read = await store.GetMessagesAsync(session.Id);
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)read.Value[0]).Content).IsEqualTo("v2");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
