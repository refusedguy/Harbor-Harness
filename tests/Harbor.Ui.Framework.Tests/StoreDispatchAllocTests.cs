using System;
using System.Collections.Generic;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #491: what the store itself costs per dispatch — the second
///     <see cref="UiState" /> copy it made only to bump the revision, and the
///     per-notification <c>EventArgs</c> + <c>GetInvocationList().Cast()</c> —
///     is measured, not asserted by inspection.
/// </summary>
/// <remarks>
///     <para>
///         Both tripwires are <b>differential</b>: they run the very same fold
///         and subtract, so the reducer's own copies (the part this issue does
///         not own) cancel out and what is left is only what
///         <see cref="UiStore" /> adds on top. That keeps the budget
///         independent of record sizes and of anything the reducers change
///         later.
///     </para>
///     <para>
///         <see cref="NotInParallelAttribute" />: TUnit runs classes in parallel
///         and <c>GC.GetAllocatedBytesForCurrentThread</c> is per-thread — a
///         neighbouring test that steals the thread mid-loop skews the
///         accounting. Serialized tripwires + min-of-3 rounds, as in
///         <c>AgentLoopAllocationTests</c>.
///     </para>
/// </remarks>
[NotInParallel("alloc-tripwire")]
public class StoreDispatchAllocTests
{
    /// <summary>
    ///     A message whose reducer fold is a fixed pair of record copies: it
    ///     never grows the transcript or an immutable collection, so the
    ///     per-dispatch byte count is constant and two stores fed the same
    ///     sequence stay byte-for-byte comparable.
    /// </summary>
    private static readonly AppMsg Fold = new AppMsg.HistoryMeasured(1);

    private const int Dispatches = 2_000;
    private const int Rounds = 3;
    private const int Warmup = 256;

    [Test]
    public async Task Notification_AddsOneArgsAllocationPerDispatch()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies across OS runtimes.

        var subscribed = new UiStore();
        subscribed.Changed += static (_, _) => { };
        var bare = new UiStore();

        // Warm both paths before accounting: a tiered-JIT recompile landing
        // inside the measured window would hit one leg only and be reported as
        // overhead the store does not have.
        for (int i = 0; i < Warmup; i++)
        {
            _ = subscribed.Dispatch(Fold);
            _ = bare.Dispatch(Fold);
        }

        long bestSubscribed = long.MaxValue;
        long bestBare = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            bestSubscribed = Math.Min(bestSubscribed, StoreBytes(subscribed));
            bestBare = Math.Min(bestBare, StoreBytes(bare));
        }

        long overhead = (bestSubscribed - bestBare) / Dispatches;
        Console.WriteLine(
            $"uistore-notify-alloc: {overhead} B per dispatch with 1 subscriber " +
            $"(subscribed {(double)bestSubscribed / Dispatches:F0} B, bare {(double)bestBare / Dispatches:F0} B, min of {Rounds})");

        // Budget: exactly one small object — the UiStateChangedEventArgs, which
        // cannot be pooled (subscribers may retain it) and is now built only
        // when somebody is listening. Pre-fix a notification also built a
        // Delegate[] and a Cast iterator, several times over this bound.
        await Assert.That(overhead).IsLessThanOrEqualTo(32L);
    }

    [Test]
    public async Task Dispatch_AddsNoStateCopyOverTheReducerFold()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only (see above).

        var store = new UiStore(); // no subscriber: Notify allocates nothing

        // Same fold, twice: once through the store (CAS + revision stamp) and
        // once straight through the reducer, on a state of the same shape.
        // Warm both first: a tiered-JIT recompile inside the window would hit
        // one leg only.
        var foldState = new UiState();
        for (int i = 0; i < Warmup; i++)
        {
            _ = store.Dispatch(Fold);
            foldState = ChatAppReducer.Update(foldState, Fold).State;
        }

        long bestStore = long.MaxValue;
        long bestReducer = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            bestStore = Math.Min(bestStore, StoreBytes(store));
            bestReducer = Math.Min(bestReducer, BytesOf(() =>
            {
                for (int i = 0; i < Dispatches; i++)
                    foldState = ChatAppReducer.Update(foldState, Fold).State;
            }));
        }

        long viaStorePer = bestStore / Dispatches;
        long viaReducerPer = bestReducer / Dispatches;
        Console.WriteLine(
            $"uistore-copy-alloc: store {viaStorePer} B vs reducer {viaReducerPer} B per dispatch (min of {Rounds}); " +
            $"store overhead {viaStorePer - viaReducerPer} B");

        // The store's own bookkeeping — CAS, revision stamp, empty fan-out —
        // must not buy a whole extra snapshot. Pre-fix this gap was exactly one
        // UiState copy (`next with { Revision = … }`).
        await Assert.That(viaStorePer - viaReducerPer).IsLessThanOrEqualTo(24L);
    }

    [Test]
    public async Task PublishedSnapshots_KeepTheRevisionTheyWereNotifiedWith()
    {
        // The single-copy path stamps the revision onto the reducer's fresh
        // instance instead of cloning it, so "the reducer always hands back a
        // snapshot it just built" is now a load-bearing invariant: stamp a
        // shared one and a subscriber's retained state would be rewritten under
        // it. This is the tripwire for that invariant.
        var store = new UiStore();
        var published = new List<UiState>();
        store.Changed += (_, e) => published.Add(e.State);

        for (int i = 0; i < 5; i++)
            store.Dispatch(new AppMsg.HistoryMeasured(i));

        await Assert.That(published.Count).IsEqualTo(5);
        for (int i = 0; i < published.Count; i++)
            await Assert.That(published[i].Revision).IsEqualTo(i + 1L);
        await Assert.That(store.State.Revision).IsEqualTo(5L);
    }

    [Test]
    public async Task SubscribeUnsubscribe_TracksTheDeliverySet()
    {
        // The delivery set is maintained by the Changed accessors instead of
        // being rebuilt per notification, so subscribe/unsubscribe bookkeeping is
        // the part that can now drift.
        var store = new UiStore();
        int first = 0;
        int second = 0;
        void First(object? _, UiStateChangedEventArgs __) => first++;
        void Second(object? _, UiStateChangedEventArgs __) => second++;

        store.Changed += First;
        store.Changed += Second;
        store.Dispatch(new AppMsg.HistoryMeasured(1));

        store.Changed -= First;
        store.Dispatch(new AppMsg.HistoryMeasured(2));

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(2);

        store.Changed -= Second;
        store.Dispatch(new AppMsg.HistoryMeasured(3));

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(2);
    }

    [Test]
    public async Task UnsubscribeDuringNotify_StillDeliversToTheSnapshot()
    {
        // Documented Notify contract, preserved across the rewrite: the delivery
        // set is snapshotted once per notification, so a subscriber that
        // unsubscribes another mid-fan-out cannot tear the set the current
        // notification runs over.
        var store = new UiStore();
        int calls = 0;
        void Second(object? _, UiStateChangedEventArgs __) => calls++;
        store.Changed += (_, _) => store.Changed -= Second;
        store.Changed += Second;

        store.Dispatch(new AppMsg.HistoryMeasured(1));
        await Assert.That(calls).IsEqualTo(1);

        store.Dispatch(new AppMsg.HistoryMeasured(2));
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task DuplicateSubscription_DropsOnePerUnsubscribe()
    {
        // Plain C# event semantics (Delegate.Combine/Remove) for the
        // hand-rolled accessors: subscribing the same handler twice delivers
        // twice, and unsubscribing drops one registration, not both.
        var store = new UiStore();
        int calls = 0;
        void Handler(object? _, UiStateChangedEventArgs __) => calls++;

        store.Changed += Handler;
        store.Changed += Handler;
        store.Dispatch(new AppMsg.HistoryMeasured(1));
        await Assert.That(calls).IsEqualTo(2);

        store.Changed -= Handler;
        store.Dispatch(new AppMsg.HistoryMeasured(2));
        await Assert.That(calls).IsEqualTo(3);
    }

    /// <summary>Bytes <paramref name="store" /> allocates over one dispatch batch.</summary>
    private static long StoreBytes(UiStore store)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Dispatches; i++)
            _ = store.Dispatch(Fold);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Bytes <paramref name="work" /> allocates over one batch.</summary>
    private static long BytesOf(Action work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
