using Harbor.Application.Configuration;

namespace Harbor.Application.Agents;

/// <summary>
///     The scheduling policy for one assistant step's tool calls (#1766):
///     which calls may share the concurrent pool, and which ones are
///     barriers that must run alone.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the policy moved out of the batch.</b> The dispatcher used to
///         decide ONCE per batch: one call declaring
///         <see cref="ExecutionMode.Sequential" /> made the WHOLE batch
///         sequential, so a step that read five files and wrote one ran six
///         serial calls where only the write needed to be alone. The decision
///         is per call now — a sequential call is a BARRIER (the parallel-safe
///         group before it drains first, it runs alone, the group after it
///         starts only once it has finished), and everything else shares a
///         pool capped at the configured maximum.
///     </para>
///     <para>
///         <b>Why there is a cap at all.</b> The batch size is chosen by the
///         model, so it is untrusted input: fifty parallel <c>read</c>s meant
///         fifty file descriptors, fifty processes or fifty sockets opened at
///         once, all driven by the CONTENT of a response. The cap is the
///         bound on that, and the excess waits for a slot instead of starting
///         at once.
///     </para>
///     <para>
///         <b>The classification is unary.</b> A call is exclusive or
///         parallel-safe, and nothing here compares siblings. Two calls that
///         conflict with each other but not with the rest of the batch (two
///         writes to the same path) are not detected — the same limitation
///         dsh records for its own per-call classification. What the barrier
///         DOES guarantee is the ordering around an exclusive call: a read
///         before a write of the same path finishes first, and a read after
///         it starts after.
///     </para>
/// </remarks>
public static class ToolBatchScheduler
{
    /// <summary>
    ///     Is this call EXCLUSIVE — a barrier that may not overlap any other
    ///     call of the same batch?
    /// </summary>
    /// <param name="resolved">
    ///     The pre-resolved tool. An unresolvable call is not exclusive: it
    ///     produces its own error entry and never touches the workspace, so
    ///     it cannot conflict with anything.
    /// </param>
    /// <remarks>
    ///     A tool that declares <see cref="ExecutionMode.Sequential" /> owns
    ///     the workspace for the length of its call.
    /// </remarks>
    public static bool IsExclusive(Result<ITool> resolved) =>
        resolved.IsSuccess && resolved.Value.ExecutionMode == ExecutionMode.Sequential;

    /// <summary>
    ///     The end of the parallel-safe group that starts at
    ///     <paramref name="start" />: the index of the first exclusive call
    ///     after it, or the batch length when the group runs to the end.
    /// </summary>
    /// <remarks>
    ///     The group is what the capped pool runs; the exclusive call at the
    ///     returned index is the barrier that follows it.
    /// </remarks>
    public static int EndOfParallelGroup(IReadOnlyList<Result<ITool>> resolved, int start)
    {
        for (int i = start; i < resolved.Count; i++)
        {
            if (IsExclusive(resolved[i]))
            {
                return i;
            }
        }

        return resolved.Count;
    }

    /// <summary>
    ///     The cap, clamped to the accepted range: a value below
    ///     <see cref="ToolSchedulingDefaults.MinMaxParallelToolCalls" /> is raised to
    ///     it, so a mis-set number serialises the batch instead of throwing
    ///     at dispatch time.
    /// </summary>
    /// <remarks>
    ///     The config store refuses such a value on load
    ///     (<see cref="ToolingConfig.Validate" />); this is the belt to that
    ///     braces, because the dispatcher is also constructed directly — by
    ///     tests, and by the agent loop's no-DI fallback.
    /// </remarks>
    public static int NormalizeCap(int maxParallelToolCalls) =>
        maxParallelToolCalls < ToolSchedulingDefaults.MinMaxParallelToolCalls
            ? ToolSchedulingDefaults.MinMaxParallelToolCalls
            : maxParallelToolCalls;
}
