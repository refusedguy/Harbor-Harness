using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Issue #402 slice 2/2 — the sent annotated image round-trips through
///     JSONL storage and reloads intact: the preamble text verbatim plus the
///     baked PNG bytes, so a reloaded session still builds the provider
///     payload the send path submitted.
/// </summary>
public class MarkupSendRoundTripTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-markup-send-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

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
    public async Task AppendThenGet_SendTurnReloadsPreambleAndPngIntact()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "kilocode", "kilo-auto/free")).Value;
            byte[] png = OnePixelPng();
            const string preamble =
                "Annotated screenshot \"broken.png\" (1 annotation), saved at /shots/broken.annotated.png. Review the marked areas.";

            var message = new UserMessage(
                "msg-markup-send",
                session.Id,
                DateTimeOffset.UtcNow,
                preamble,
                "code",
                "kilo-auto/free",
                Attachments: [new ImageAttachment("/shots/broken.annotated.png", "image/png", 1, 1, png)]);

            var appended = await store.AppendMessageAsync(session.Id, message);
            await Assert.That(appended.IsSuccess).IsTrue();

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);

            var reloaded = (UserMessage)messages.Value[0];
            await Assert.That(reloaded.Content).IsEqualTo(preamble);
            await Assert.That(reloaded.HasAttachments).IsTrue();

            var image = reloaded.Attachments![0];
            await Assert.That(image.MimeType).IsEqualTo("image/png");
            await Assert.That(image.Path).IsEqualTo("/shots/broken.annotated.png");
            await Assert.That(Convert.ToBase64String(image.Data)).IsEqualTo(Convert.ToBase64String(png));
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
