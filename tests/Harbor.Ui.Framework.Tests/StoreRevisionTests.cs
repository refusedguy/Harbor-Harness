using System;
using System.Collections.Generic;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #94, item 1: CAS success and <c>Changed</c> delivery are not
///     atomic, so every transition carries a monotonic revision and
///     subscribers drop stale (out-of-order) notifications.
/// </summary>
public class StoreRevisionTests
{
    [Test]
    public async Task Dispatch_BumpsRevisionMonotonically()
    {
        var store = new UiStore();
        var revisions = new List<long>();
        store.Changed += (_, e) => revisions.Add(e.Revision);

        store.Dispatch(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        store.Dispatch(new CompactionStartedEvent("s1"));
        store.Dispatch(new AgentErrorEvent("boom"));

        await Assert.That(revisions.Count).IsEqualTo(3);
        await Assert.That(revisions[0]).IsEqualTo(1);
        await Assert.That(revisions[1]).IsEqualTo(2);
        await Assert.That(revisions[2]).IsEqualTo(3);
        await Assert.That(store.State.Revision).IsEqualTo(3);
    }

    [Test]
    public async Task EventArgs_RevisionMatchesPublishedState()
    {
        var store = new UiStore();
        UiStateChangedEventArgs? last = null;
        store.Changed += (_, e) => last = e;

        store.Dispatch(new CompactionStartedEvent("s1"));

        await Assert.That(last).IsNotNull();
        await Assert.That(last!.Revision).IsEqualTo(last.State.Revision);
        await Assert.That(last.State.Status).IsEqualTo("compacting");

        // Reset is a transition too: the revision keeps climbing, never resets to zero.
        store.Reset();
        await Assert.That(store.State.Revision).IsEqualTo(2);
    }

    [Test]
    public async Task OutOfOrderDelivery_StaleNotificationDropped()
    {
        var store = new UiStore();
        var delivered = new List<UiStateChangedEventArgs>();
        store.Changed += (_, e) => delivered.Add(e);

        store.Dispatch(new CompactionStartedEvent("s1")); // revision 1
        store.Dispatch(new AgentErrorEvent("boom")); // revision 2

        // Simulate cross-thread reorder: revision 2 arrives first, revision 1 late.
        long lastApplied = 0;
        UiState? current = null;
        void Apply(UiStateChangedEventArgs e)
        {
            if (e.IsStale(lastApplied))
                return;
            lastApplied = e.Revision;
            current = e.State;
        }

        Apply(delivered[1]);
        Apply(delivered[0]); // stale — must not rewind

        await Assert.That(lastApplied).IsEqualTo(2);
        await Assert.That(current).IsNotNull();
        await Assert.That(current!.Revision).IsEqualTo(2);
        await Assert.That(current.Lines.Length).IsEqualTo(1);
    }
}
