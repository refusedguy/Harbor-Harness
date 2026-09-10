using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #46: frequency invariants per N streaming deltas on the store path.
///     Guards the "is the projector on fire" question: with per-delta
///     projection pacing, the overwhelming majority of Project calls must hit
///     the same-screen fast path; tail rebuilds track flushes (≪ deltas) and
///     history rebuilds stay O(folds), not O(deltas).
/// </summary>
public class StreamingFrequencyTests
{
    private static (int Projects, int Fast, int Tail, int History) Drive(int deltaCount)
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();
        var partial = AssistantMessage.Empty("s", "m");
        store.Dispatch(new MessageStartEvent(partial));

        int projects = 0;
        int fast = 0;
        int tail = 0;
        int history = 0;
        UiScreenModel? prevScreen = null;
        var prevLines = store.State.Lines;
        string? prevBuffer = store.State.Active.TextBuffer;
        string chunk = new('x', 24);

        for (int i = 0; i < deltaCount; i++)
        {
            store.Dispatch(new MessageUpdateEvent(new TextDeltaEvent("m", chunk), partial));
            var state = store.State;
            var screen = projector.Project(state);
            projects++;

            if (ReferenceEquals(screen, prevScreen))
            {
                fast++;
            }

            if (!state.Lines.Equals(prevLines))
            {
                history++;
                prevLines = state.Lines;
            }

            if (!ReferenceEquals(state.Active.TextBuffer, prevBuffer))
            {
                tail++;
                prevBuffer = state.Active.TextBuffer;
            }

            prevScreen = screen;
        }

        store.Dispatch(new MessageEndEvent(partial));
        var endScreen = projector.Project(store.State);
        projects++;
        if (ReferenceEquals(endScreen, prevScreen))
        {
            fast++;
        }

        if (!store.State.Lines.Equals(prevLines))
        {
            history++;
        }

        return (projects, fast, tail, history);
    }

    [Test]
    public async Task ThousandDeltas_ProjectorStaysIncremental()
    {
        var (projects, fast, tail, history) = Drive(1000);

        // Measured 2026-09-10: projects=1001, fast=962, tail=38, history=1.
        // Bounds (not exact counts — flush policy constants may evolve):
        await Assert.That(projects).IsEqualTo(1001);
        await Assert.That(history).IsLessThanOrEqualTo(2);
        await Assert.That(tail).IsLessThanOrEqualTo(100);
        await Assert.That(fast).IsGreaterThanOrEqualTo(projects * 9 / 10);
    }
}
