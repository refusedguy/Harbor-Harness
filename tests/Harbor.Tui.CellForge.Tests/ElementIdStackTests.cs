// #2436: the frame's id stack and the typed element-state table. Both are
// engine primitives, so the tests drive their own public-to-the-assembly API:
// push/pop plus the resulting id, and get/try-get plus the live slot count.
// The allocation claim is measured on one thread inside a synchronous helper;
// the measurement window never crosses an await.

namespace Harbor.Tui.CellForge.Tests;

public class ElementIdStackTests
{
    private sealed class Counter
    {
        internal int Value;
    }

    private sealed class OtherCounter
    {
        internal int Value;
    }

    [Test]
    public async Task StackIds_MatchChildIds_AndPopRestoresTheParentPath()
    {
        var stack = new EngineCells.ElementIdStack();
        var expected = EngineCells.ElementId.Root;

        stack.Push("row");
        expected = expected.Child("row");
        await Assert.That(stack.GlobalId).IsEqualTo(expected);
        await Assert.That(stack.GlobalId.PrefixHash).IsEqualTo(expected.PrefixHash);
        await Assert.That(stack.Depth).IsEqualTo(1);

        stack.Push(2);
        expected = expected.Child(2);
        await Assert.That(stack.GlobalId).IsEqualTo(expected);
        await Assert.That(stack.GlobalId.PrefixHash).IsEqualTo(expected.PrefixHash);

        stack.Push("text");
        expected = expected.Child("text");
        await Assert.That(stack.GlobalId).IsEqualTo(expected);
        await Assert.That(stack.GlobalId.ToString()).IsEqualTo("/row/2/text");

        stack.Pop();
        await Assert.That(stack.GlobalId.ToString()).IsEqualTo("/row/2");
        await Assert.That(stack.GlobalId.PrefixHash).IsEqualTo(EngineCells.ElementId.Root.Child("row").Child(2).PrefixHash);

        stack.Pop();
        stack.Pop();
        await Assert.That(stack.GlobalId).IsEqualTo(EngineCells.ElementId.Root);
        await Assert.That(stack.Depth).IsEqualTo(0);
    }

    [Test]
    public async Task StateStore_KeepsOneSlotPerIdAndType()
    {
        var store = new EngineCells.ElementStateStore();
        var id = EngineCells.ElementId.Root.Child("panel");
        var other = EngineCells.ElementId.Root.Child("other");

        store.BeginFrame();
        var counter = store.GetOrCreate<Counter>(id);
        var sameSlot = store.GetOrCreate<Counter>(id);
        var otherType = store.GetOrCreate<OtherCounter>(id);
        var otherId = store.GetOrCreate<Counter>(other);
        counter.Value = 7;
        store.EndFrame();

        await Assert.That(sameSlot).IsSameReferenceAs(counter);
        await Assert.That(otherType).IsNotSameReferenceAs(counter);
        await Assert.That(otherId).IsNotSameReferenceAs(counter);
        await Assert.That(store.Count).IsEqualTo(3);

        store.BeginFrame();
        await Assert.That(store.TryGet<Counter>(id, out var again)).IsTrue();
        await Assert.That(again).IsSameReferenceAs(counter);
        await Assert.That(again!.Value).IsEqualTo(7);
        await Assert.That(store.TryGet<OtherCounter>(other, out _)).IsFalse();
        store.EndFrame();
    }

    [Test]
    public async Task StateStore_EndFrame_DropsSlotsNoElementAccessed()
    {
        var store = new EngineCells.ElementStateStore();
        var first = EngineCells.ElementId.Root.Child("keep");
        var second = EngineCells.ElementId.Root.Child("drop");

        store.BeginFrame();
        _ = store.GetOrCreate<Counter>(first);
        _ = store.GetOrCreate<Counter>(second);
        store.EndFrame();
        await Assert.That(store.Count).IsEqualTo(2);

        store.BeginFrame();
        _ = store.GetOrCreate<Counter>(first);
        store.EndFrame();
        await Assert.That(store.Count).IsEqualTo(1);

        store.BeginFrame();
        await Assert.That(store.TryGet<Counter>(second, out _)).IsFalse();
        store.EndFrame();
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task StateStoredByStackId_IsReadableByTheEquivalentChildId()
    {
        var stack = new EngineCells.ElementIdStack();
        var store = new EngineCells.ElementStateStore();
        stack.Push("row");
        stack.Push(0);

        store.BeginFrame();
        var state = store.GetOrCreate<Counter>(stack.GlobalId);
        state.Value = 42;
        store.EndFrame();

        var viaChild = EngineCells.ElementId.Root.Child("row").Child(0);
        store.BeginFrame();
        await Assert.That(store.TryGet<Counter>(viaChild, out var found)).IsTrue();
        await Assert.That(found!.Value).IsEqualTo(42);
        store.EndFrame();
    }

    [Test]
    public async Task RepeatingOnePath_AllocatesNothingInSteadyState()
    {
        long bytes = MeasureRepeatedPath();
        await Assert.That(bytes).IsEqualTo(0);
    }

    [Test]
    public async Task AllocationProbe_DetectsANewPath()
    {
        long bytes = MeasureNewPath();
        await Assert.That(bytes > 0).IsTrue();
    }

    // Every frame walks the same three steps and touches the same state slot.
    // A warm-up run settles dictionary capacity and JIT before the counter read.
    private static long MeasureRepeatedPath()
    {
        var stack = new EngineCells.ElementIdStack();
        var store = new EngineCells.ElementStateStore();
        string[] steps = ["row", "body", "text"];

        Walk(stack, store, steps, 16);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Walk(stack, store, steps, 64);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The same probe must see a cost when the tree actually changes, otherwise
    // the zero above would say nothing about the work being measured.
    private static long MeasureNewPath()
    {
        var stack = new EngineCells.ElementIdStack();
        var store = new EngineCells.ElementStateStore();
        string[] steps = ["row", "body", "text"];
        Walk(stack, store, steps, 8);

        long before = GC.GetAllocatedBytesForCurrentThread();
        stack.Clear();
        store.BeginFrame();
        stack.Push("new-step");
        _ = store.GetOrCreate<Counter>(stack.GlobalId);
        store.EndFrame();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Walk(
        EngineCells.ElementIdStack stack,
        EngineCells.ElementStateStore store,
        string[] steps,
        int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            stack.Clear();
            store.BeginFrame();

            stack.Push(steps[0]);
            stack.Push(0);
            stack.Push(steps[2]);
            var state = store.GetOrCreate<Counter>(stack.GlobalId);
            state.Value++;
            _ = store.TryGet<Counter>(stack.GlobalId, out _);

            stack.Pop();
            stack.Pop();
            stack.Pop();
            store.EndFrame();
        }
    }
}
