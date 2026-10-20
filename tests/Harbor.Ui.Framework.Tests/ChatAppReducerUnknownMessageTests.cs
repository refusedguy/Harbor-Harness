using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using Harbor.TestKit;

namespace Harbor.Ui.Framework.Tests;
/// <summary>
///     #461 — the history replay in <see cref="ChatAppReducer" /> used to end in
///     a <c>_ =&gt; state</c> arm, so a message role the reducer did not know was
///     dropped from the replayed transcript without a word. The walk now goes
///     through <see cref="AgentMessageVisitor{TResult}" />, which refuses it.
/// </summary>
public class ChatAppReducerUnknownMessageTests
{
    [Test]
    public async Task AgentStart_Replay_DropsAFilePartFromTheTextLineListAsBefore()
    {
        var assistant = TestMessages.AssistantParts(new ContentPart[] { new TextPart("hello"), new FilePart("a.png", "image/png", 1) }, id: "m1", sessionId: "s", createdAt: DateTimeOffset.UnixEpoch);
        AgentMessage[] history = new[] { assistant };

        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.Agent(TestEvents.AgentStart("s", history)));

        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(result.State.Chat.Lines[0].Text).IsEqualTo("hello");
    }

    [Test]
    public async Task AgentStart_Replay_UnknownMessageRole_RefusesIt()
    {
        AgentMessage[] history = new[] { new RogueMessage("m1", "s", DateTimeOffset.UnixEpoch) };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ChatAppReducer.Update(new UiState(), new ChatAppMsg.Agent(TestEvents.AgentStart("s", history))));

        await Assert.That(ex.Message.Contains("RogueMessage", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AgentStart_Replay_UnknownPartKind_RefusesIt()
    {
        var assistant = TestMessages.AssistantParts(new ContentPart[] { new RoguePart("payload") }, id: "m1", sessionId: "s", createdAt: DateTimeOffset.UnixEpoch);
        AgentMessage[] history = new[] { assistant };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ChatAppReducer.Update(new UiState(), new ChatAppMsg.Agent(TestEvents.AgentStart("s", history))));

        await Assert.That(ex.Message.Contains("RoguePart", StringComparison.Ordinal)).IsTrue();
    }

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
