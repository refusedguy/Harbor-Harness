using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     #2461: the request body carries full prompts and tool arguments, which
///     frequently contain PII and file contents. Debug logging may report the
///     request's SHAPE (model, message and tool counts), but must never copy
///     the serialized body into a log sink.
/// </summary>
public class RequestPayloadLoggingSafetyTests
{
    private const string SecretPromptMarker = "PROMPT_SECRET_MARKER_9F2A71";

    [Test]
    public async Task BuildRequest_DebugEnabled_LogsShapeButNotPromptContent()
    {
        var logger = new CapturingLogger<OpenAiCompatibleLlmClient>(LogLevel.Trace);
        var client = new OpenAiCompatibleLlmClient(
            new HttpClient(),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            new TestAuthResolver(),
            FakeModelCatalog.Instance,
            logger);

        var request = new LlmRequest(
            Model: "test-model",
            Messages: [LlmUserMessage.Text(SecretPromptMarker)],
            SystemPrompt: "sys",
            Tools: []);

        using var message = client.BuildRequest(request, "test-key");

        await Assert.That(logger.Capture.Messages.Any(m => m.Contains("BuildRequest:"))).IsTrue().Because(
            "the shape line is the diagnostic operators need at Debug level");
        await Assert.That(logger.Capture.Contains(SecretPromptMarker)).IsFalse().Because(
            "logging the serialized body would leak the full prompt into every log sink");
    }
}
