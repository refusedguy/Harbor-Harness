using Harbor.Abstractions.Events;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     E27: the event union carries its own schema version and a journaled
///     marker so a replayed fact and a live semantic event can share the union
///     without a consumer guessing which one it is reading.
/// </summary>
public class AgentEventSchemaVersionTests
{
    [Test]
    public async Task NewEvent_DefaultsToCurrentSchemaVersion_AndIsNotJournaled()
    {
        AgentEvent evt = new TurnStartEvent(1, "session-1");

        await Assert.That(evt.SchemaVersion).IsEqualTo(1);
        await Assert.That(evt.IsJournaled).IsFalse();
    }

    [Test]
    public async Task WithExpression_MarksReplayWithoutMutatingOriginal()
    {
        AgentEvent original = new TurnStartEvent(1, "session-1");
        AgentEvent replay = original with { IsJournaled = true, SchemaVersion = 1 };

        await Assert.That(replay.IsJournaled).IsTrue();
        await Assert.That(replay.SchemaVersion).IsEqualTo(1);
        await Assert.That(original.IsJournaled).IsFalse();
    }
}
