using System.Reflection;
using System.Text;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #493: the write-back at the end of
///     <c>StreamingCoalescer.AppendToolCallDelta</c> stored the pending-call
///     entry back into the table after appending to its pooled builder.
///     <c>PooledStringBuilder</c> is a <c>readonly struct</c> wrapping a shared
///     <see cref="StringBuilder" />, so the append had <b>already</b> mutated
///     the object the entry points at: the entry's tuple cannot have changed.
///     The store was a hash lookup plus a bucket write per fragment — on the
///     largest delta stream in a turn — with no semantic effect, and it made
///     the class <i>look</i> as though it needed copy-on-write semantics, which
///     it does not.
/// </summary>
/// <remarks>
///     <para>
///         The removed line was removed, and what makes that safe is pinned
///         here from two sides.
///     </para>
///     <para>
///         <b>1. Nothing outside the class can observe the table.</b>
///         <c>PendingCallTable_HasNoObserverOutsideTheCoalescer</c> guards the
///         field's accessibility and the absence of any public/internal member
///         that mentions a dictionary — so "is a store needed to keep some
///         cache coherent?" can only be answered no. This half is a
///         <i>static</i> property of the class and therefore holds both before
///         and after the fix; that is the point, it is what makes the store
///         unnecessary rather than merely unobserved.
///     </para>
///     <para>
///         <b>2. The one reader stays coherent with no per-delta
///         mutation.</b> <c>MaterializeToolCalls</c> is the only consumer of the
///         table, and
///         <c>ToolCallDelta_AppendsEveryFragment_AndTheOnlyReaderStillSeesThemAll</c>
///         pins that it still sees every fragment in order, across a call that
///         never writes to the table.
///         <c>EntryHeldFromBeforeTheFragments_SeesThemAll_WithoutAWriteBack</c>
///         is the same claim from the other side: a copy of the entry taken
///         <i>before</i> the fragments arrived observes all of them anyway,
///         because the table is a table of references. That is precisely why
///         nothing had to be re-stored per delta.
///     </para>
///     <para>
///         Note on instrument choice: a <see cref="Dictionary{TKey,TValue}" />
///         enumerator was tried first and does <b>not</b> work — overwriting an
///         existing key does not bump the collection version, so the enumerator
///         stays valid across the very store this issue is about (pinned by
///         <c>OverwritingAnExistingKey_LeavesTheEnumeratorValid_NotAModificationProbe</c>).
///         There is no cheap way to observe a same-key store from outside, which
///         is the cleanest possible statement of how little that line was doing:
///         it was not even a modification. An allocation budget would not have
///         caught it either — a hash plus a bucket write is 0 B. Hence the
///         surface guard + behavioural pairing above.
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
            "StreamingCoalescer._pendingToolCalls is gone — re-pin the #493 no-store tests against the new field.");

    private static readonly MethodInfo StartToolCallMethod =
        CoalescerType.GetMethod("StartToolCall", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("StreamingCoalescer.StartToolCall is gone — re-pin the #493 tests.");

    private static readonly MethodInfo AppendToolCallDeltaMethod =
        CoalescerType.GetMethod("AppendToolCallDelta", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("StreamingCoalescer.AppendToolCallDelta is gone — re-pin the #493 tests.");

    private static readonly MethodInfo MaterializeToolCallsMethod =
        CoalescerType.GetMethod("MaterializeToolCalls", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("StreamingCoalescer.MaterializeToolCalls is gone — re-pin the #493 tests.");

    [Test]
    public async Task PendingCallTable_HasNoObserverOutsideTheCoalescer()
    {
        // The type itself is internal, so nothing outside the assembly can even
        // name it.
        await Assert.That(CoalescerType.IsNotPublic).IsTrue();

        // The table is a private instance field: not public, not internal, not
        // protected — so no subclass or friend assembly can reach it either.
        await Assert.That(PendingCallsField.IsPrivate).IsTrue();
        await Assert.That(PendingCallsField.IsStatic).IsFalse();

        // ...and no public or internal member hands the table, or anything
        // that reads as one, out of the class. A "subscriber" here would be
        // exactly such a member; with none, a store into the table cannot be
        // what any outside observer depends on.
        var leaks = CoalescerType
            .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(VisibleOutside)
            .Where(MentionsADictionary)
            .Select(m => $"{m.MemberType} {m}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert.That(leaks).IsEmpty();
    }

    [Test]
    public async Task ToolCallDelta_AppendsEveryFragment_AndTheOnlyReaderStillSeesThemAll()
    {
        // MaterializeToolCalls is the table's only consumer. The per-delta
        // write-back is gone, and this is the property it was not protecting:
        // the accumulated args are still the fragments, in order, byte for
        // byte — no fragment lost, duplicated or reordered on the way to the
        // single parse the materialization does.
        object coalescer = NewCoalescer();
        try
        {
            StartToolCallMethod.Invoke(coalescer, new object?[] { CallId, "read" });

            var expected = new StringBuilder();
            for (int i = 0; i < Fragments; i++)
            {
                string fragment = (i & 1) == 0 ? OpenArgs : CloseArgs;
                AppendToolCallDeltaMethod.Invoke(coalescer, new object?[] { CallId, fragment });
                expected.Append(fragment);
            }

            // One call, one entry: a fragment never smuggled a second pending
            // call (or a replacement builder) in under cover of the append.
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
        // The half that makes the store redundant rather than merely
        // unobserved: an observer that copied the entry out before the
        // fragments arrived sees all of them anyway, because the entry wraps a
        // StringBuilder that Append mutates in place. Re-storing an identical
        // tuple could not have told that observer anything new — so no cache
        // needed it, and none should reintroduce it.
        object coalescer = NewCoalescer();
        try
        {
            StartToolCallMethod.Invoke(coalescer, new object?[] { CallId, "read" });
            object table = PendingCallsField.GetValue(coalescer)!;

            StringBuilder held = ArgsBuilderOf(table);
            var expected = new StringBuilder();
            for (int i = 0; i < Fragments; i++)
            {
                AppendToolCallDeltaMethod.Invoke(coalescer, new object?[] { CallId, SingleCharFragment });
                expected.Append(SingleCharFragment);
            }

            // The stale holder is current, without anything having been
            // re-stored for it to observe.
            await Assert.That(held.ToString()).IsEqualTo(expected.ToString());

            // ...and the table still points at that very builder, so a reader
            // arriving fresh sees the same bytes, and the tool name never moved.
            await Assert.That(ReferenceEquals(ArgsBuilderOf(table), held)).IsTrue();
            await Assert.That(NameOf(table)).IsEqualTo("read");
        }
        finally
        {
            ((IDisposable)coalescer).Dispose();
        }
    }

    [Test]
    public async Task OverwritingAnExistingKey_LeavesTheEnumeratorValid_NotAModificationProbe()
    {
        // The empirical half of the note above, kept as a test so nobody
        // re-derives it: overwriting an EXISTING key does not bump the
        // dictionary's modification version, so an enumerator armed before
        // the store still walks the table afterwards. That is why a
        // version/enumeration probe cannot see the line #493 removed — and
        // why the guards here are a surface check plus behaviour instead.
        var replica = new Dictionary<string, (string Name, StringBuilder Args)>(capacity: 4);
        replica[CallId] = ("read", new StringBuilder());

        System.Collections.IEnumerator probe = ((System.Collections.IEnumerable)replica).GetEnumerator();
        await Assert.That(probe.MoveNext()).IsTrue();

        var acc = replica[CallId];
        acc.Args.Append("""{"path":"README.md"}""");
        replica[CallId] = acc; // the write-back #493 removed

        // Still valid: the second MoveNext simply reports the end of the table.
        await Assert.That(probe.MoveNext()).IsFalse();

        // ...and the mutation is visible through the entry all the same,
        // which is the whole point — it is reference mutation, not a store.
        await Assert.That(replica[CallId].Args.ToString()).IsEqualTo("""{"path":"README.md"}""");
    }

    private static object NewCoalescer() => Activator.CreateInstance(CoalescerType, nonPublic: true)!;

    /// <summary>Whether a member is callable from outside the class (public or internal).</summary>
    private static bool VisibleOutside(MemberInfo member) => member switch
    {
        FieldInfo f => f.IsPublic || f.IsAssembly,
        PropertyInfo p => (p.GetMethod ?? p.SetMethod) is { } a && (a.IsPublic || a.IsAssembly),
        MethodInfo m => m.IsPublic || m.IsAssembly,
        _ => false,
    };

    /// <summary>Whether a member's signature mentions a dictionary (the shape of the pending table).</summary>
    private static bool MentionsADictionary(MemberInfo member) => member switch
    {
        FieldInfo f => IsDictionary(f.FieldType),
        PropertyInfo p => IsDictionary(p.PropertyType),
        MethodInfo m => IsDictionary(m.ReturnType) || m.GetParameters().Any(p => IsDictionary(p.ParameterType)),
        _ => false,
    };

    private static bool IsDictionary(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(IDictionary<,>));

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
                "PooledStringBuilder.Builder is gone — re-pin the #493 stale-entry test against the new shape.");
        return (StringBuilder)builder.GetValue(pooled)!;
    }

    /// <summary>The tool name the (single) pending entry was started with.</summary>
    private static string NameOf(object table)
    {
        foreach (object? entry in (System.Collections.IEnumerable)table)
        {
            object tuple = EntryTuple(entry!);
            return (string)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        }

        throw EmptyTable();
    }

    /// <summary>The <c>PooledStringBuilder</c> the (single) pending entry holds.</summary>
    private static object PooledWrapperOf(object table)
    {
        foreach (object? entry in (System.Collections.IEnumerable)table)
        {
            object tuple = EntryTuple(entry!);
            return tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
        }

        throw EmptyTable();
    }

    private static object EntryTuple(object keyValuePair) =>
        keyValuePair.GetType().GetField("Item2")!.GetValue(keyValuePair)!;

    private static InvalidOperationException EmptyTable() =>
        new("The pending-call table is empty — the #493 test under it is broken.");
}
