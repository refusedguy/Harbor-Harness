using System;
using System.Threading;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Dev-only swap slot for a generated <c>BuildRoot</c> (issue #2435).
///     A generated root checks <see cref="Current"/> inside a <c>#if DEBUG</c>
///     block and, when it is set, calls it with its three sink methods instead
///     of running the compiled body. The host, event loop, capability probe and
///     terminal mode are never touched by the swap.
/// </summary>
/// <remarks>
///     <para>
///         The slot holds an <see cref="Action{T1, T2, T3}"/> over method
///         groups, not compiled code. A dev observer parses the changed markup
///         and installs a delegate that replays the new tree into the sink.
///         There is no reflection, no <c>dynamic</c> and no runtime emit, so
///         NativeAOT is unaffected; Release and AOT builds compile the
///         generated check out with <c>DEBUG</c>.
///     </para>
///     <para>
///         The arguments are <c>begin(sequence)</c>,
///         <c>text(sequence, value)</c> and <c>end()</c>. They carry the same
///         dense pre-order sequence numbers <c>BuildRoot</c> emits as cell-diff
///         keys, so a replay keeps those keys stable across a swap.
///     </para>
/// </remarks>
public static class RootBuilderOverride
{
    private static Action<Action<int>, Action<int, string>, Action>? _current;

    /// <summary>The builder that replaces a compiled root body, or null.</summary>
    public static Action<Action<int>, Action<int, string>, Action>? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>
    ///     Installs <paramref name="builder"/> and returns the previous value
    ///     so a dev session can restore it. Setting null clears the slot.
    /// </summary>
    public static Action<Action<int>, Action<int, string>, Action>? Install(
        Action<Action<int>, Action<int, string>, Action>? builder) =>
        Interlocked.Exchange(ref _current, builder);

    /// <summary>Idempotent clear, used after a failed or reverted reload.</summary>
    public static void Clear() => Volatile.Write(ref _current, null);
}
