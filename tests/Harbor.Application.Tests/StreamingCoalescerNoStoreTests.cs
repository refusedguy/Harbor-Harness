using System.Collections;
using System.Reflection;
using System.Text;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #493: <c>StreamingCoalescer.AppendToolCallDelta</c> must not write to
///     the pending-tool-call table. The table holds
///     <c>(string Name, PooledStringBuilder Args)</c> tuples, and
///     <c>PooledStringBuilder</c> is a readonly struct wrapping a shared
///     <see cref="StringBuilder" /> — so <c>acc.Args.Builder.Append(delta)</c>
///     mutates the very object the stored entry already points at, the entry's
///     tuple cannot have changed, and re-storing it was a hash + bucket write
///     per fragment with no semantic effect. Args fragments are the largest
///     delta stream in a turn, so this sat directly on the hot path — and it
///     made the class <i>look</i> like it needed copy-on-write semantics, which
///     it does not.
/// </summary>
/// <remarks>
///     <para>
///         The instrument is <see cref="Dictionary{TKey,TValue}" />'s own
///         documented modification contract rather than a private field: a
///         dictionary enumerator is invalidated by any add, remove or
///         <b>update</b>, so "an enumerator armed before the fragments still
///         walks the table afterwards" <i>is</i> "the table was not written".
///         Public API, no BCL-internal reflection, and it also catches a
///         re-rent of the args builder per fragment, not just the exact line
///         that was deleted.
///     </para>
///     <para>
///         <c>OverwritingAnEntry_InvalidatesTheSameProbe_TripwireIsNotVacuous</c>
///         replays the removed write-back against a local replica of the
///         table's shape through the same probe, so a probe that could never
///         fire cannot make the gate meaningful.
///     </para>
///     <para>
///         The internal coalescer is reached by reflection: this project has
///         no <c>InternalsVisibleTo</c> for <c>Harbor.Application</c> — adding
///         one leaks the ZLinq drop-ins into every file here and breaks
///         overload resolution elsewhere (see <c>StreamingCoalescerPoolTests</c>).
///         Nothing here reads a GC counter or touches shared state, so the
///         class needs no <c>NotInParallel</c> serialization.
///     </para>
/// </remarks>
public class StreamingCoalescerNoStoreTests
{
    private const string CallId = "call-1";
    private const int Fragments = 64;

    /// <summary>The two args fragments, interleaved: together they are one valid args JSON.</summary>
    private const string OpenArgs = "{\"path\":";

    private const string CloseArgs = "\"src/Harbor/README.md\"}";
    private const string SingleCharFragment = "x";

    private static readonly Type CoalescerType =
        typeof(AgentLoop).Assembly.GetType("Harbor.Application.Agents.StreamingCoalescer", throwOnError: true)!;

    private static readonly FieldInfo PendingCallsField =
        CoalescerType.GetField("_pendingToolCalls", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "StreamingCoalescer._pendingToolCalls is gone — re-pin the #493 no-store probe against the new field.");

    private static readonly MethodInfo StartToolCallMethod =
        CoalescerType.GetMethod("StartToolCall", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("StreamingCoalescer.StartToolCall is gone — re-pin the #493 probe.");

    private static readonly MethodInfo AppendToolCallDeltaMethod =
        CoalescerType.GetMethod("AppendToolCallDelta", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("StreamingCoalescer.AppendToolCallDelta is gone — re-pin the #493 probe.");

    private static readonly MethodInfo MaterializeToolCallsMethod =
        CoalescerType.GetMethod("MaterializeToolCalls", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("StreamingCoalescer.MaterializeToolCalls is gone — re-pin the #493 probe.");

    [Test]
    public async Task ToolCallDelta_AppendsEveryFragment_WithoutWritingToTheTable()
    {
        object coalescer = NewCoalescer();
        try
        {
            StartToolCallMethod.Invoke(coalescer, new object?[] { CallId, "read" });
            object table = PendingCallsField.GetValue(coalescer)!;

            // Arm the probe while the table holds exactly the entry
            // StartToolCall created. The table has one entry, so the next
            // MoveNext would return false rather than visit anything new —
            // any modification turns that into an InvalidOperationException.
            IEnumerator probe = ((IEnumerable)table).GetEnumerator();
            await Assert.That(probe.MoveNext()).IsTrue();

            var expected = new StringBuilder();
            for (int i = 0; i < Fragments; i++)
            {
                string fragment = (i & 1) == 0 ? OpenArgs : CloseArgs;
                AppendToolCallDeltaMethod.Invoke(coalescer, new object?[] { CallId, fragment });
                expected.Append(fragment);
            }

            // 1. No store. A per-delta `_pendingToolCalls[id] = acc` would have
            //    invalidated the enumerator armed above.
            await Assert.That(Invalidated(probe)).IsFalse();

            // 2. Still the one entry StartToolCall made — no fragment
            //    smuggled a replacement builder (or a second call) into the
            //    table under cover of an unobserved write.
            await Assert.That(((ICollection)table).Count).IsEqualTo(1);

            // 3. ...and the table's only reader is still coherent, with no
            //    per-delta store: every fragment lands, in order.
            var calls = MaterializeToolCallsMethod.Invoke(coalescer, new object?[] { null }) as List<ToolCallPart>;
            await Assert.That(calls).IsNotNull();
            await Assert.That(calls!.Count).IsEqualTo(1);
            await Assert.That(calls[0].Id).IsEqualTo(CallId);
            await Assert.That(calls[0].ToolName).IsEqualTo("read");
            await Assert.That(calls[0].Args.GetRawText()).IsEqualTo(expected.ToString());
        }
        finally
        {
            ((IDisposable)coalescer).Dispose();
        }
    }

    [Test]
    public async Task EntryHeldFromBeforeTheFragments_SeesThemAll_WithoutAWriteBack()
    {
        // The other half of "the write-back is unobservable": the table is a
        // table of references. An observer that copied the entry out before the
        // fragments arrived sees all of them anyway, because the entry wraps a
        // StringBuilder that Append mutates in place — which is why no cache
        // had to be kept coherent by a store per delta.
        object coalescer = NewCoalescer();
        try
        {
            StartToolCallMethod.Invoke(coalescer, new object?[] { CallId, "read" });
            object table = PendingCallsField.GetValue(coalescer)!;

            StringBuilder held = ArgsBuilderOf(table);
            var expected = new StringBuilder();
            for (int i = 0; i < Fragments; i++)
            {
                string fragment = SingleCharFragment;
                AppendToolCallDeltaMethod.Invoke(coalescer, new object?[] { CallId, fragment });
                expected.Append(fragment);
            }

            // The stale holder is current: nothing had to be re-stored for it
            // to observe the mutation.
            await Assert.That(held.ToString()).IsEqualTo(expected.ToString());

            // ...and the table still points at that very builder, so a reader
            // coming in fresh sees the same bytes.
            await Assert.That(ReferenceEquals(ArgsBuilderOf(table), held)).IsTrue();
            await Assert.That(NameOf(table)).IsEqualTo("read");
        }
        finally
        {
            ((IDisposable)coalescer).Dispose();
        }
    }

    [Test]
    public async Task OverwritingAnEntry_InvalidatesTheSameProbe_TripwireIsNotVacuous()
    {
        // The statement #493 deleted, verbatim, against a local replica of the
        // table's shape and through the probe the gate above uses: an indexer
        // overwrite of an EXISTING key — the store that looks like the
        // load-bearing one — must register as a modification. Without this, a
        // probe that cannot fire would leave the gate above meaningless.
        var replica = new Dictionary<string, (string Name, StringBuilder Args)>(capacity: 4);
        replica[CallId] = ("read", new StringBuilder());

        IEnumerator probe = ((IEnumerable)replica).GetEnumerator();
        await Assert.That(probe.MoveNext()).IsTrue();

        var acc = replica[CallId];
        acc.Args.Append("""{"path":"README.md"}""");
        replica[CallId] = acc; // the write-back #493 removed

        await Assert.That(Invalidated(probe)).IsTrue();
    }

    private static object NewCoalescer() => Activator.CreateInstance(CoalescerType, nonPublic: true)!;

    /// <summary>
    ///     Whether <paramref name="probe" /> is no longer valid — i.e. whether
    ///     the table was modified (entry added, removed or overwritten) after
    ///     the probe was armed.
    /// </summary>
    private static bool Invalidated(IEnumerator probe)
    {
        try
        {
            _ = probe.MoveNext();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    ///     The <see cref="StringBuilder" /> the (single) pending entry points
    ///     at, reached through the boxed
    ///     <c>KeyValuePair&lt;string, (string Name, PooledStringBuilder Args)&gt;</c>
    ///     the table's enumerator hands out: pair &rarr; tuple &rarr; pooled
    ///     wrapper &rarr; builder.
    /// </summary>
    private static StringBuilder ArgsBuilderOf(object table)
    {
        object pooled = PooledWrapperOf(table);
        PropertyInfo builder = pooled.GetType().GetProperty("Builder")
            ?? throw new InvalidOperationException(
                "PooledStringBuilder.Builder is gone — re-pin the #493 stale-entry probe against the new shape.");
        return (StringBuilder)builder.GetValue(pooled)!;
    }

    /// <summary>The tool name the (single) pending entry was started with.</summary>
    private static string NameOf(object table)
    {
        foreach (object? entry in (IEnumerable)table)
        {
            object tuple = EntryTuple(entry!);
            return (string)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        }

        throw EmptyTable();
    }

    /// <summary>The <c>PooledStringBuilder</c> the (single) pending entry holds.</summary>
    private static object PooledWrapperOf(object table)
    {
        foreach (object? entry in (IEnumerable)table)
        {
            object tuple = EntryTuple(entry!);
            return tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
        }

        throw EmptyTable();
    }

    private static object EntryTuple(object keyValuePair) =>
        keyValuePair.GetType().GetField("Item2")!.GetValue(keyValuePair)!;

    private static InvalidOperationException EmptyTable() =>
        new("The pending-call table is empty — the #493 probe under test is broken.");
}
