using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;
/// <summary>
///     #461 — the LLM conversion used to drop an unrecognised
///     <see cref="ContentPart" /> on the floor: <c>ConvertParts</c> had no
///     <c>default:</c> arm, so a turn that carried a part the converter had never
///     seen was sent to the provider with that part silently missing.
/// </summary>
/// <remarks>
///     The walk now runs through <see cref="ContentPartVisitor{TResult}" />, which
///     refuses a kind it has no arm for. <see cref="FilePart" /> keeps its own
///     explicit no-op arm (assistant-side files have no LLM block form) — what
///     changed is that a NEW kind can no longer inherit that silence by accident.
/// </remarks>
public class MessageConverterUnknownPartTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task ToLlmMessages_AssistantTurnWithAKnownFilePart_DropsItAsBefore()
    {
        var assistant = Assistant(new ContentPart[] { new FilePart("shots/a.png", "image/png", 42) });

        var result = new MessageConverter().ToLlmMessages(new AgentMessage[] { assistant });

        await Assert.That(result.Count).IsEqualTo(1);
        var assistantMsg = (LlmAssistantMessage)result[0];
        await Assert.That(assistantMsg.Content.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task ToLlmMessages_AssistantTurnWithAnUnknownPart_RefusesIt()
    {
        var assistant = Assistant(new ContentPart[] { new RoguePart("payload") });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new MessageConverter().ToLlmMessages(new AgentMessage[] { assistant }));

        await Assert.That(ex.Message.Contains("RoguePart", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ToLlmMessages_UnknownMessageRole_RefusesIt()
    {
        AgentMessage[] messages = new[] { new RogueMessage("m1", "session-1", Ts) };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new MessageConverter().ToLlmMessages(messages));

        await Assert.That(ex.Message.Contains("RogueMessage", StringComparison.Ordinal)).IsTrue();
    }

    private static AssistantMessage Assistant(IReadOnlyList<ContentPart> parts) =>
        new("m1", "session-1", Ts, parts, StopReason.Stop, new Usage(0, 0), "test-model");

    private sealed record RoguePart(string Text) : ContentPart
    {
        public override string Type => "rogue";
    }

    private sealed record RogueMessage(string Id, string SessionId, DateTimeOffset CreatedAt)
        : AgentMessage(Id, SessionId, CreatedAt)
    {
        public override string Role => "rogue";
    }
}
