extern alias CompatWire;

using System.Net.Http;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Ollama;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #2605: one owner for "first text or empty". The shared helper
///     scans the whole list in one pass and emits at most one drop warning per
///     message, including when the dropped block sits BEHIND a text block.
///     Ollama no longer carries a second, silent copy of the rule.
/// </summary>
public class FirstTextOrEmptyUnificationTests
{
    private static string Flatten(IReadOnlyList<LlmContentBlock> content, CapturingLogger logger) =>
        CompatWire::Harbor.Providers.Internal.ProviderPayload.FirstTextOrEmpty(content, logger, "stub");

    [Test]
    public async Task ImageBehindText_WarnsOnceAndKeepsTheFirstText()
    {
        var logger = new CapturingLogger();

        string text = Flatten(
            [new LlmTextBlock("look"), new LlmImageBlock("image/png", [1, 2, 3]), new LlmTextBlock("later")],
            logger);

        await Assert.That(text).IsEqualTo("look");
        await Assert.That(logger.Warnings.Count).IsEqualTo(1)
            .Because("the single pass must report the image BEHIND the first text block, once");
    }

    [Test]
    public async Task SeveralNonTextBlocks_WarnOnlyOnce()
    {
        var logger = new CapturingLogger();

        string text = Flatten(
            [new LlmImageBlock("image/png", [1]), new LlmImageBlock("image/png", [2])],
            logger);

        await Assert.That(text).IsEqualTo("");
        await Assert.That(logger.Warnings.Count).IsEqualTo(1)
            .Because("the drop warning is per message, not per dropped block");
    }

    [Test]
    public async Task EmptyTextBlock_IsNotADroppedBlock()
    {
        var logger = new CapturingLogger();

        string text = Flatten([new LlmTextBlock(""), new LlmTextBlock("second")], logger);

        await Assert.That(text).IsEqualTo("second")
            .Because("the first NON-EMPTY text block wins");
        await Assert.That(logger.Warnings.Count).IsEqualTo(0)
            .Because("an empty text block is still a text block, not dropped content");
    }

    [Test]
    public async Task Ollama_ImageOnlyUserTurn_WarnsThroughTheSharedHelper()
    {
        var logger = new CapturingLogger<OllamaLlmClient>();
        var client = new OllamaLlmClient(new HttpClient(), new OllamaConfig(), logger);
        var request = new LlmRequest(
            Model: "llama3",
            Messages: [new LlmUserMessage([new LlmImageBlock("image/png", [1])])],
            SystemPrompt: "",
            Tools: []);

        using HttpRequestMessage message = client.BuildRequest(request);
        string body = await message.Content!.ReadAsStringAsync();

        await Assert.That(body).Contains("\"content\":\"\"")
            .Because("the image is dropped from the compact string form, leaving an empty prompt");
        await Assert.That(logger.Warnings.Count).IsEqualTo(1)
            .Because("Ollama must use the one shared helper, so a dropped image is loud here too");
    }
}
