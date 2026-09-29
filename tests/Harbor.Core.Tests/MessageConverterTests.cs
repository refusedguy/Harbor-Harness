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

    // ---------------------------------------------------------------------
    // #623 — the provider-bound payload must not grow super-linearly.
    //
    // The reported symptom was a "growing prompt": ctx climbing 30k → 150k over
    // five turns. The history is *not* the culprit — ToLlmMessages emits only the
    // three kinds AgentMessage can hold (user / assistant / tool result) and no
    // system message exists in the union, so the system prompt cannot leak in
    // here. These two tests pin that: no system role ever appears, and the
    // provider-bound payload grows by a constant per turn.
    // ---------------------------------------------------------------------

    private const int Turns = 8;

    /// <summary>A tool-using session of <paramref name="turns" /> turns: user →
    /// assistant(tool call) → tool result, the shape that actually grows a
    /// transcript. Every turn carries the same payload, so a constant per-turn
    /// delta is the correct expectation.</summary>
    private static List<AgentMessage> BuildSession(int turns)
    {
        var messages = new List<AgentMessage>(turns * 3);
        for (int t = 0; t < turns; t++)
        {
            string callId = $"call-{t}";
            messages.Add(new UserMessage($"u{t}", SessionId, Ts, $"question number {t}", "code", "test-model"));
            messages.Add(new AssistantMessage(
                $"a{t}", SessionId, Ts,
                [
                    new TextPart($"answer number {t}"),
                    new ToolCallPart(callId, "read", ParseJson($$"""{"path":"/tmp/file-{{t}}.cs"}"""))
                ],
                StopReason.ToolUse,
                new Usage(10, 5),
                "test-model"));
            messages.Add(new ToolResultMessage(
                $"r{t}", SessionId, Ts,
                [new ToolResultEntry(callId, "read", $"contents of file {t}", false)]));
        }

        return messages;
    }

    /// <summary>Characters carried by a message's content blocks.</summary>
    private static int ContentChars(IReadOnlyList<LlmContentBlock> blocks)
    {
        int total = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            total += blocks[i] switch
            {
                LlmTextBlock t => t.Text.Length,
                LlmToolCallBlock c => c.Id.Length + c.Name.Length + c.Arguments.GetRawText().Length,
                _ => 0
            };
        }

        return total;
    }

    /// <summary>
    ///     Size of the provider-bound payload in characters: every role string,
    ///     every text block, tool-call argument JSON, tool-result output, and a
    ///     fixed per-message envelope standing in for JSON syntax. Counted
    ///     explicitly because the declared block type (<see cref="LlmContentBlock" />)
    ///     is abstract, so a plain JsonSerializer.Serialize would flatten the
    ///     graph to its base shape and measure nothing.
    /// </summary>
    private static int WireChars(IReadOnlyList<LlmMessage> messages)
    {
        const int EnvelopePerMessage = 16;
        int total = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            total += m.Role.Length + EnvelopePerMessage;

            switch (m)
            {
                case LlmUserMessage u:
                    total += ContentChars(u.Content);
                    break;

                case LlmAssistantMessage a:
                    total += ContentChars(a.Content);
                    break;

                case LlmToolResultMessage r:
                    total += r.ToolCallId.Length + r.ToolName.Length + r.Output.Length;
                    break;
            }
        }

        return total;
    }

    [Test]
    public async Task ToLlmMessages_AcrossManyTurns_EmitsNoSystemRole()
    {
        // A system message cannot be constructed (AgentMessage is a closed union
        // of User/Assistant/ToolResult), so a "system" role here would mean the
        // converter invented one — the leak this guards against.
        var converter = new MessageConverter();
        for (int turn = 1; turn <= Turns; turn++)
        {
            var result = converter.ToLlmMessages(BuildSession(turn));

            for (int i = 0; i < result.Count; i++)
            {
                await Assert.That(result[i].Role).IsNotEqualTo("system");
            }

            // Only the two roles the union can produce survive conversion
            // (tool results are wire-role "user").
            for (int i = 0; i < result.Count; i++)
            {
                var role = result[i].Role;
                await Assert.That(role == "user" || role == "assistant").IsTrue();
            }
        }
    }

    [Test]
    public async Task ToLlmMessages_AcrossManyTurns_SerializedLengthGrowsByConstantPerTurn()
    {
        // Linear growth, pinned exactly: identical turn payloads must each add
        // the same number of characters, and N turns must total exactly N times
        // one turn. A re-sent system prompt or any per-turn re-wrapping would
        // make the deltas grow and blow past this bound.
        var converter = new MessageConverter();

        int previous = 0;
        int firstTurnDelta = 0;

        for (int turn = 1; turn <= Turns; turn++)
        {
            int length = WireChars(converter.ToLlmMessages(BuildSession(turn)));
            int delta = length - previous;

            if (turn == 1)
            {
                firstTurnDelta = delta;
            }
            else
            {
                await Assert.That(delta).IsEqualTo(firstTurnDelta);
            }

            previous = length;
        }

        await Assert.That(firstTurnDelta).IsGreaterThan(0);
        await Assert.That(previous).IsEqualTo(firstTurnDelta * Turns);
    }
}
