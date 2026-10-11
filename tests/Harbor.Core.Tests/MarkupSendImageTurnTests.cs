using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;

namespace Harbor.Core.Tests;

/// <summary>
///     Issue #402 slice 2/2 — the send-back turn is an ordinary image turn:
///     the preamble text rides as the first block verbatim (source name,
///     annotation count, saved path) and the baked PNG as the
///     <see cref="LlmImageBlock" /> after it, through the exact
///     domain→LLM hop every provider builder already serialises. Sending the
///     same annotation set twice is two identical turns (expected), never one
///     turn with a duplicated block.
/// </summary>
public class MarkupSendImageTurnTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47];

    private const string Preamble =
        "Annotated screenshot \"broken.png\" (2 annotations), saved at /shots/broken.annotated.png. Review the marked areas.";

    private static UserMessage SendTurn(string id) => new(
        id, "s1", Ts, Preamble, "code", "test-model",
        Attachments: [new ImageAttachment("/shots/broken.annotated.png", "image/png", 40, 20, PngBytes)]);

    [Test]
    public async Task ToLlmMessages_SendTurn_EmitsPreambleThenImageBlock()
    {
        var converter = new MessageConverter();

        var result = converter.ToLlmMessages([SendTurn("m1")]);

        var llm = (LlmUserMessage)result[0];
        await Assert.That(llm.Content.Count).IsEqualTo(2);

        var text = (LlmTextBlock)llm.Content[0];
        await Assert.That(text.Text).IsEqualTo(Preamble);

        var image = (LlmImageBlock)llm.Content[1];
        await Assert.That(image.MimeType).IsEqualTo("image/png");
        await Assert.That(image.Data).IsEquivalentTo(PngBytes);
    }

    [Test]
    public async Task ToLlmMessages_ResendSameAnnotations_ProducesTwoTurns_OneBlockEach()
    {
        var converter = new MessageConverter();

        var result = converter.ToLlmMessages([SendTurn("m1"), SendTurn("m2")]);

        await Assert.That(result.Count).IsEqualTo(2);
        foreach (var message in result)
        {
            var llm = (LlmUserMessage)message;
            await Assert.That(llm.Content.Count).IsEqualTo(2);
            await Assert.That(((LlmTextBlock)llm.Content[0]).Text).IsEqualTo(Preamble);
            await Assert.That(llm.Content[1]).IsTypeOf<LlmImageBlock>();
        }
    }
}
