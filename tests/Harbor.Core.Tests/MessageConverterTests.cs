using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;
namespace Harbor.Core.Tests;
/// <summary>
///     Tests for <see cref="MessageConverter.ToLlmMessages" /> — verifies that each
///     domain <see cref="AgentMessage" /> subtype is mapped to the correct
///     <see cref="LlmMessage" /> shape, with content blocks translated losslessly.
/// </summary>
public class MessageConverterTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    private static readonly string SessionId = "session-1";

    private static JsonElement ParseJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task ToLlmMessages_EmptyList_ReturnsEmptyList()
    {
        var converter = new MessageConverter();
        var result = converter.ToLlmMessages(Array.Empty<AgentMessage>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ToLlmMessages_UserMessage_ConvertsToLlmUserMessage()
    {
        var converter = new MessageConverter();
        var user = new UserMessage("m1", SessionId, Ts, "Hello, agent!", "code", "test-model");

        var result = converter.ToLlmMessages(new AgentMessage[] { user });

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]).IsTypeOf<LlmUserMessage>();
        var userMsg = (LlmUserMessage)result[0];
        await Assert.That(userMsg.Role).IsEqualTo("user");
        await Assert.That(userMsg.Content.Count).IsEqualTo(1);
        var textBlock = (LlmTextBlock)userMsg.Content[0];
        await Assert.That(textBlock.Text).IsEqualTo("Hello, agent!");
    }

    [Test]
    public async Task ToLlmMessages_AssistantMessage_ConvertsToLlmAssistantMessage()
    {
        var converter = new MessageConverter();
        var assistant = new AssistantMessage(
            "m2",
            SessionId,
            Ts,
            new ContentPart[]
            {
                new TextPart("Hello back!"),
                new ThinkingPart("Reasoning about the response"),
                new ToolCallPart("call-1", "read", ParseJson("""{"path":"/tmp/x"}"""))
            },
            StopReason.ToolUse,
            new Usage(10, 5),
            "test-model");

        var result = converter.ToLlmMessages(new AgentMessage[] { assistant });

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]).IsTypeOf<LlmAssistantMessage>();
        var llm = (LlmAssistantMessage)result[0];
        await Assert.That(llm.Role).IsEqualTo("assistant");
        await Assert.That(llm.Content.Count).IsEqualTo(3);

        await Assert.That(llm.Content[0]).IsTypeOf<LlmTextBlock>();
        await Assert.That(((LlmTextBlock)llm.Content[0]).Text).IsEqualTo("Hello back!");

        await Assert.That(llm.Content[1]).IsTypeOf<LlmThinkingBlock>();
        await Assert.That(((LlmThinkingBlock)llm.Content[1]).Text).IsEqualTo("Reasoning about the response");

        await Assert.That(llm.Content[2]).IsTypeOf<LlmToolCallBlock>();
        var callBlock = (LlmToolCallBlock)llm.Content[2];
        await Assert.That(callBlock.Id).IsEqualTo("call-1");
        await Assert.That(callBlock.Name).IsEqualTo("read");
        await Assert.That(callBlock.Arguments.GetRawText()).IsEqualTo("""{"path":"/tmp/x"}""");

        // StopReason is serialized as the wire-format lowercase string (snake_case).
        await Assert.That(llm.StopReason).IsEqualTo("tool_use");
    }

    [Test]
    public async Task ToLlmMessages_ToolResultMessage_ConvertsToLlmToolResultMessage()
    {
        var converter = new MessageConverter();
        var toolResult = new ToolResultMessage(
            "m3",
            SessionId,
            Ts,
            new[]
            {
                new ToolResultEntry("call-1", "read", "file contents here", false),
                new ToolResultEntry("call-2", "bash", "command failed", true)
            });

        var result = converter.ToLlmMessages(new AgentMessage[] { toolResult });

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsTypeOf<LlmToolResultMessage>();
        await Assert.That(result[1]).IsTypeOf<LlmToolResultMessage>();

        var first = (LlmToolResultMessage)result[0];
        await Assert.That(first.ToolCallId).IsEqualTo("call-1");
        await Assert.That(first.ToolName).IsEqualTo("read");
        await Assert.That(first.Output).IsEqualTo("file contents here");
        await Assert.That(first.IsError).IsFalse();

        var second = (LlmToolResultMessage)result[1];
        await Assert.That(second.ToolCallId).IsEqualTo("call-2");
        await Assert.That(second.ToolName).IsEqualTo("bash");
        await Assert.That(second.Output).IsEqualTo("command failed");
        await Assert.That(second.IsError).IsTrue();
    }

    [Test]
    public async Task ToLlmMessages_MixedMessages_PreservesOrder()
    {
        var converter = new MessageConverter();
        var messages = new AgentMessage[]
        {
            new UserMessage("u1", SessionId, Ts, "first", "code", "test-model"),
            new AssistantMessage(
                "a1", SessionId, Ts,
                new[] { new TextPart("second") },
                StopReason.Stop,
                new Usage(0, 0),
                "test-model"),
            new UserMessage("u2", SessionId, Ts, "third", "code", "test-model")
        };

        var result = converter.ToLlmMessages(messages);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0]).IsTypeOf<LlmUserMessage>();
        await Assert.That(result[1]).IsTypeOf<LlmAssistantMessage>();
        await Assert.That(result[2]).IsTypeOf<LlmUserMessage>();

        var first = (LlmUserMessage)result[0];
        await Assert.That(((LlmTextBlock)first.Content[0]).Text).IsEqualTo("first");
        var second = (LlmAssistantMessage)result[1];
        await Assert.That(((LlmTextBlock)second.Content[0]).Text).IsEqualTo("second");
        var third = (LlmUserMessage)result[2];
        await Assert.That(((LlmTextBlock)third.Content[0]).Text).IsEqualTo("third");
    }

    [Test]
    public async Task ToLlmMessages_AssistantMessage_EmptyParts_ConvertsToEmptyContent()
    {
        var converter = new MessageConverter();
        var assistant = new AssistantMessage(
            "a2", SessionId, Ts,
            Array.Empty<ContentPart>(),
            StopReason.Stop,
            new Usage(0, 0),
            "test-model");

        var result = converter.ToLlmMessages(new AgentMessage[] { assistant });

        await Assert.That(result.Count).IsEqualTo(1);
        var llm = (LlmAssistantMessage)result[0];
        await Assert.That(llm.Content.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ToLlmMessages_ToolResultMessage_EmptyResults_ProducesNoMessages()
    {
        // An empty ToolResultMessage contributes zero LlmToolResultMessages — the
        // foreach over an empty Results array simply doesn't add anything.
        var converter = new MessageConverter();
        var empty = new ToolResultMessage("m4", SessionId, Ts, Array.Empty<ToolResultEntry>());

        var result = converter.ToLlmMessages(new AgentMessage[] { empty });

        await Assert.That(result.Count).IsEqualTo(0);
    }
}
