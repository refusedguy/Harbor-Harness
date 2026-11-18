using Harbor.Abstractions.Events;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Tests.Pilot;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2704 H03: the headless harness must not swallow background failures.
///     A fault in an <see cref="IEventBus"/> subscriber reaches the caller of
///     <see cref="CellForgePilot.PublishAsync"/> instead of being parked as a
///     quiet error event, and it must not strand the bus drain.
/// </summary>
/// <remarks>
///     The rest of the pilot suite asserts only the happy path. These tests pin
///     the failure path directly through the pilot's real bus + hook seam, so a
///     refactor of the event loop that starts catching subscriber exceptions
///     turns a test red instead of silently dropping the error.
/// </remarks>
public sealed class PilotExceptionContractTests
{
    [Test]
    public async Task ThrowingSubscriber_PropagatesOutOfPublishAsync()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        using var subscription = pilot.Hook((_, _) => throw new InvalidOperationException("subscriber boom"));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pilot.PublishAsync(TestEvents.AgentStart()));

        await Assert.That(thrown.Message).IsEqualTo("subscriber boom");
    }

    [Test]
    public async Task ThrowingSubscriber_FailedEventIsRecorded_ButNoErrorEventIsInvented()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        using var subscription = pilot.Hook((_, _) => throw new InvalidOperationException("subscriber boom"));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pilot.PublishAsync(TestEvents.AgentEnd()));

        await Assert.That(pilot.SeenEvents.Count).IsEqualTo(1);
        await Assert.That(pilot.SeenEvents[0]).IsTypeOf<AgentEndEvent>();
        await Assert.That(pilot.SeenEvents.Any(e => e is AgentErrorEvent)).IsFalse()
            .Because("a swallowed failure must not be replaced by a quiet error event");
        await Assert.That(pilot.Bus.UnpublishedEventCount).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingSubscriber_DrainStillCompletes()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        using var subscription = pilot.Hook((_, _) => throw new InvalidOperationException("subscriber boom"));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pilot.PublishAsync(TestEvents.AgentStart()));

        await pilot.Bus.WaitForDrainAsync(TimeSpan.FromSeconds(1));
        await Assert.That(pilot.Bus.UnpublishedEventCount).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingTypedHook_FailsOnlyForMatchingEvent()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        using var subscription = pilot.Hook<AgentErrorEvent>(
            (_, _) => throw new InvalidOperationException("typed subscriber boom"));

        // The typed hook ignores other events, so the non-matching publish is clean.
        await pilot.PublishAsync(TestEvents.AgentStart());

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pilot.PublishAsync(TestEvents.AgentError("boom")));

        await Assert.That(thrown.Message).IsEqualTo("typed subscriber boom");
    }
}
