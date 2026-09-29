namespace Harbor.Ui.Framework;

/// <summary>
///     Lifecycle phase of a single tool invocation — the one vocabulary for
///     "what state is this tool call in", shared by the TEA state, the
///     view-models, the projection layer and every renderer (#567).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why these names.</b> The three enums this replaced disagreed on
///         spelling: <c>Success</c> (view-models) vs <c>Done</c> (projection)
///         vs <c>Ok</c> (CellForge). The domain already had a word for it —
///         <c>ToolResult.Success</c> / <c>ToolResult.Error</c> in
///         <c>Harbor.Abstractions.Contracts</c> — so <see cref="Success"/>
///         and <see cref="Error"/> win: the renderer vocabulary now names
///         itself after the wire vocabulary instead of paraphrasing it. The
///         short <c>"ok"</c> / <c>"err"</c> strings survive as <em>display</em>
///         text (see <c>StatusMappers.ToolCallStateToPill</c>), which is the
///         layer where brevity belongs.
///     </para>
///     <para>
///         <b>Which states are terminal.</b> <see cref="Success"/>,
///         <see cref="Error"/>, <see cref="Cancelled"/> and
///         <see cref="TimedOut"/> end the call; <see cref="Pending"/> and
///         <see cref="Running"/> do not. The classification lives once, in
///         <see cref="ToolCallStateExtensions.IsTerminal"/>, instead of
///         being re-derived per call site. The old three-enum spread had no
///         such statement, which is why a cancelled call could not be told
///         apart from a running one.
///     </para>
///     <para>
///         <b>Member set.</b> The union of all three vocabularies, so
///         translating a legacy value loses nothing: <c>Pending</c> (the
///         projection's only non-running pre-flight state, dropped by the
///         other two), <c>Running</c>, <c>Success</c>/<c>Done</c>/<c>Ok</c>,
///         <c>Error</c>, plus <c>Cancelled</c> and <c>TimedOut</c> — the two
///         terminal states a call can stop in without producing a result.
///         Before this enum neither of them was expressible, so a stopped
///         call fell into a wildcard arm and painted as still running.
///     </para>
///     <para>
///         <b>How a new member is prevented from being handled silently.</b>
///         C# cannot express "exhaustive over the named members" for a switch
///         over an enum: the compiler also demands the unnamed domain, and
///         CS8524 is an unconditional error — so a discard-less switch
///         expression does not compile at all. The gate is therefore layered:
///         every switch over this enum names all members explicitly and then
///         throws on the unnamed domain (a <c>(ToolCallState)99</c> cast is a
///         bug, not a state to render), so an unmapped value is loud instead
///         of silently becoming "running"; and
///         <c>ToolCallStateGuardTests</c> walks
///         <c>Enum.GetValues&lt;ToolCallState&gt;()</c> asserting every member
///         has a pill, a brush, a glyph and a terminal classification, so
///         adding a member is a test failure rather than a spinner that never
///         stops.
///     </para>
/// </remarks>
public enum ToolCallState
{
    /// <summary>Call is known but not executing yet (no start event seen).</summary>
    Pending,

    /// <summary>Tool is currently executing.</summary>
    Running,

    /// <summary>Tool completed and produced a successful result.</summary>
    Success,

    /// <summary>Tool ran and reported an error result.</summary>
    Error,

    /// <summary>Call was stopped before producing a result (user abort, supersede).</summary>
    Cancelled,

    /// <summary>Call was stopped by its deadline.</summary>
    TimedOut
}

/// <summary>
///     Classification helpers for <see cref="ToolCallState" />. Kept beside the
///     enum (not per-call-site) so "is this call finished?" has one answer.
/// </summary>
public static class ToolCallStateExtensions
{
    /// <summary>
    ///     True when the call can no longer change state — the card must stop
    ///     its spinner. All members named explicitly; the unnamed domain throws
    ///     rather than defaulting to a guess (#567).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="state"/> is not a declared member.
    /// </exception>
    public static bool IsTerminal(this ToolCallState state) => state switch
    {
        ToolCallState.Pending or ToolCallState.Running => false,
        ToolCallState.Success or ToolCallState.Error
            or ToolCallState.Cancelled or ToolCallState.TimedOut => true,
        _ => throw Undeclared(state)
    };

    /// <summary>
    ///     The single throw site for an out-of-domain
    ///     <see cref="ToolCallState"/> value, shared by every switch over the
    ///     enum across assemblies so the failure message is identical wherever
    ///     it surfaces — and so no call site is tempted to write its own
    ///     plausible-looking default.
    /// </summary>
    public static ArgumentOutOfRangeException Undeclared(ToolCallState state) =>
        new(nameof(state), state,
            $"'{state}' is not a declared ToolCallState. Adding a member means naming it in every switch over the enum — no wildcard arm is allowed to invent a default (#567).");
}
