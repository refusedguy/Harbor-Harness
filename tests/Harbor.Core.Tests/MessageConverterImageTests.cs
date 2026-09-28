using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;

namespace Harbor.Core.Tests;

/// <summary>
///     Issue #386 — the domain→LLM hop is where an attached image becomes an
///     <see cref="LlmImageBlock" />. Text-only turns must keep the exact
///     single-block shape they had before, so every existing provider payload
///     stays byte-identical.
/// </summary>
public class MessageConverterImageTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47];

    [Test]
    public async Task ToLlmMessages_TextOnlyUser_KeepsTheSingleTextBlockShape()
    {
        var converter = new MessageConverter();
        var user = new UserMessage("m1", "s1", Ts, "hello", "code", "test-model");

        var result = converter.ToLlmMessages([user]);

        var llm = (LlmUserMessage)result[0];
        await Assert.That(llm.Content.Count).IsEqualTo(1);
        await Assert.That(llm.Content[0]).IsTypeOf<LlmTextBlock>();
    }

    [Test]
    public async Task ToLlmMessages_ImageTurn_EmitsTextThenImageBlocks()
    {
        var converter = new MessageConverter();
        var user = new UserMessage(
            "m1", "s1", Ts, "what is this?", "code", "test-model",
            Attachments: [new ImageAttachment("/tmp/a.png", "image/png", 4, 2, PngBytes)]);

        var result = converter.ToLlmMessages([user]);

        var llm = (LlmUserMessage)result[0];
        await Assert.That(llm.Content.Count).IsEqualTo(2);

        var text = (LlmTextBlock)llm.Content[0];
        await Assert.That(text.Text).IsEqualTo("what is this?");

        var image = (LlmImageBlock)llm.Content[1];
        await Assert.That(image.MimeType).IsEqualTo("image/png");
        await Assert.That(image.Data).IsEquivalentTo(PngBytes);
    }

    [Test]
    public async Task ToLlmMessages_MultipleImages_PreserveOrderAndMimeTypes()
    {
        var converter = new MessageConverter();
        var user = new UserMessage(
            "m1", "s1", Ts, "compare", "code", "test-model",
            Attachments:
            [
                new ImageAttachment("/tmp/a.png", "image/png", 1, 1, [0x01]),
                new ImageAttachment("/tmp/b.jpg", "image/jpeg", 2, 2, [0x02])
            ]);

        var result = converter.ToLlmMessages([user]);

        var llm = (LlmUserMessage)result[0];
        await Assert.That(llm.Content.Count).IsEqualTo(3);
        await Assert.That(((LlmImageBlock)llm.Content[1]).MimeType).IsEqualTo("image/png");
        await Assert.That(((LlmImageBlock)llm.Content[2]).MimeType).IsEqualTo("image/jpeg");
    }

    [Test]
    public async Task ToLlmMessages_EmptyAttachmentList_BehavesLikeATextOnlyTurn()
    {
        var converter = new MessageConverter();
        var user = new UserMessage("m1", "s1", Ts, "hi", "code", "test-model", Attachments: []);

        var result = converter.ToLlmMessages([user]);

        var llm = (LlmUserMessage)result[0];
        await Assert.That(llm.Content.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UserMessage_HasAttachments_TracksPresence()
    {
        var without = new UserMessage("m1", "s1", Ts, "hi", "code", "m");
        await Assert.That(without.HasAttachments).IsFalse();

        var with = without with
        {
            Attachments = [new ImageAttachment("/a.png", "image/png", 1, 1, PngBytes)]
        };
        await Assert.That(with.HasAttachments).IsTrue();
    }
}
