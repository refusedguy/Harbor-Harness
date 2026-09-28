using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Issue #386 — an image attached to a user turn must survive the JSONL
///     round-trip with its MIME type, dimensions and bytes intact, so a
///     reloaded session can still build the provider payload.
/// </summary>
public class JsonlImageAttachmentTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    private static string SessionFile(JsonlSessionStore store, Session session) =>
        Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");

    /// <summary>Minimal but structurally real 1×1 PNG (signature + IHDR + IDAT + IEND).</summary>
    private static byte[] OnePixelPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    [Test]
    public async Task AppendThenGet_ImageAttachmentRoundTripsIntact()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "kilocode", "kilo-auto/free")).Value;
            byte[] png = OnePixelPng();

            var message = new UserMessage(
                "msg-img",
                session.Id,
                DateTimeOffset.UtcNow,
                "what is in this screenshot?",
                "code",
                "kilo-auto/free",
                Attachments: [new ImageAttachment("/tmp/shot.png", "image/png", 1, 1, png)]);

            var appended = await store.AppendMessageAsync(session.Id, message);
            await Assert.That(appended.IsSuccess).IsTrue();

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);

            var reloaded = (UserMessage)messages.Value[0];
            await Assert.That(reloaded.Content).IsEqualTo("what is in this screenshot?");
            await Assert.That(reloaded.HasAttachments).IsTrue();

            var image = reloaded.Attachments![0];
            await Assert.That(image.MimeType).IsEqualTo("image/png");
            await Assert.That(image.Width).IsEqualTo(1);
            await Assert.That(image.Height).IsEqualTo(1);
            await Assert.That(image.Data.Length).IsEqualTo(png.Length);
            await Assert.That(Convert.ToBase64String(image.Data)).IsEqualTo(Convert.ToBase64String(png));
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task TextOnlyTurn_StaysOnThePreImageWireShape()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "msg-plain", session.Id, DateTimeOffset.UtcNow, "hi", "code", "claude-opus-4"));

            string line = File.ReadAllText(SessionFile(store, session))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.Contains("msg-plain", StringComparison.Ordinal));
            await Assert.That(line).DoesNotContain("attachments");
            await Assert.That(line).Contains("\"content\":\"hi\"");

            var messages = await store.GetMessagesAsync(session.Id);
            var reloaded = (UserMessage)messages.Value[0];
            await Assert.That(reloaded.HasAttachments).IsFalse();
            await Assert.That(reloaded.Attachments).IsNull();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task MalformedAttachmentEntry_IsSkippedAndTheTurnStillLoads()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "msg-broken", session.Id, DateTimeOffset.UtcNow, "hello", "code", "claude-opus-4"));

            // An entry missing mimeType/data must not make the turn unreadable —
            // a broken image degrades to a text-only turn, it does not drop it.
            string file = SessionFile(store, session);
            File.AppendAllText(file,
                "{\"type\":\"message\",\"id\":\"msg-broken-2\",\"parentId\":null,\"role\":\"user\"," +
                "\"createdAt\":\"2026-01-01T00:00:00+00:00\",\"payload\":{\"content\":\"with image\"," +
                "\"agent\":\"code\",\"model\":\"claude-opus-4\",\"attachments\":[{\"path\":\"/x.png\"}]}}\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(2);

            var withImage = messages.Value.OfType<UserMessage>().First(m => m.Id == "msg-broken-2");
            await Assert.That(withImage.Content).IsEqualTo("with image");
            await Assert.That(withImage.HasAttachments).IsFalse();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task GetMessagesAsync_ReadsTheImageThroughTheSpanFastPath()
    {
        // The store's read path is the span-based JsonlLineParser, NOT the
        // JsonElement codec — both must honour attachments, so exercise the one
        // that actually runs in production. The line is hand-written (it never
        // went through the writer) to pin the on-disk shape as well.
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            byte[] png = OnePixelPng();
            string base64 = Convert.ToBase64String(png);

            string line =
                "{\"type\":\"message\",\"id\":\"m-span\",\"parentId\":null,\"role\":\"user\"," +
                "\"createdAt\":\"2026-01-01T00:00:00+00:00\",\"payload\":{\"content\":\"span path\"," +
                "\"agent\":\"code\",\"model\":\"claude-opus-4\",\"attachments\":[" +
                "{\"path\":\"/a.png\",\"mimeType\":\"image/png\",\"width\":1,\"height\":1," +
                // Plain concatenation, NOT an interpolated string: inside $"…"
                // a "}}" collapses to a single '}' and silently drops the
                // line-closing brace, which the parser then reports as an
                // unterminated object instead of a malformed fixture.
                "\"data\":\"" + base64 + "\"}]}}";

            // Assert on the parser's own diagnostic, not just a boolean: a bare
            // IsTrue() would hide WHY a hand-written line was rejected.
            var direct = JsonlLineParser.Parse(System.Text.Encoding.UTF8.GetBytes(line), session.Id);
            await Assert.That(direct.IsFailure ? direct.Error : "ok").IsEqualTo("ok");

            // Seed the session file through the store first (header line and a
            // valid message), so the hand-written line is the only new one.
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m-seed", session.Id, DateTimeOffset.UtcNow, "seed", "code", "claude-opus-4"));

            string file = SessionFile(store, session);
            File.AppendAllText(file, line + "\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(2);

            // Select by id, not by index: the store orders messages by
            // CreatedAt, and the hand-written line is dated 2026-01-01 while the
            // seed message is "now" — so the fixture line sorts FIRST.
            var message = messages.Value.OfType<UserMessage>().First(m => m.Id == "m-span");
            await Assert.That(message.HasAttachments).IsTrue();
            await Assert.That(message.Attachments).IsNotNull();

            var image = message.Attachments![0];
            await Assert.That(image.MimeType).IsEqualTo("image/png");
            await Assert.That(image.Width).IsEqualTo(1);
            await Assert.That(image.Height).IsEqualTo(1);
            await Assert.That(image.Path).IsEqualTo("/a.png");
            // Base64 comparison, not collection equivalence: a length mismatch
            // must surface as a readable assertion, not an indexer crash.
            await Assert.That(Convert.ToBase64String(image.Data)).IsEqualTo(base64);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
