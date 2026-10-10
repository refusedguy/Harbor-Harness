namespace Harbor.Application.Sessions;

/// <summary>
///     Legal-transition map for the verified-change run lifecycle (epic #42,
///     slice S9, #397): <c>Pinned -> Isolated -> Changed -> Checked ->
///     Reported -> Accepted | Rejected</c>, with <c>Isolated -> Released</c>
///     and <c>Rejected -> Released</c> as the worktree-removal paths.
///     Every transition is fail-closed: an illegal move is a
///     <c>Result.Failure</c> carrying a named reason
///     (<c>cannot apply a run that is not Reported</c>), never a silent
///     no-op. Later slices (S3–S8) call this map before touching the
///     manifest; they do not reinterpret it.
/// </summary>
public static class RunChangeTransitions
{
    /// <summary>
    ///     Terminal states: no outgoing transition is legal from them.
    /// </summary>
    public static bool IsTerminal(RunState state) =>
        state is RunState.Accepted or RunState.Released;

    /// <summary>
    ///     Validate one lifecycle step. Success carries the target state so
    ///     callers can persist exactly what was validated.
    /// </summary>
    public static Result<RunState> TryTransition(RunState from, RunState to)
    {
        if (IsLegal(from, to))
            return Result.Success(to);
        return Result.Failure<RunState>(
            $"Illegal run transition {from} -> {to}: {Reason(from, to)}.");
    }

    private static bool IsLegal(RunState from, RunState to) =>
        (from, to) switch
        {
            (RunState.Pinned, RunState.Isolated) => true,
            (RunState.Isolated, RunState.Changed) => true,
            (RunState.Isolated, RunState.Released) => true,
            (RunState.Changed, RunState.Checked) => true,
            (RunState.Checked, RunState.Reported) => true,
            (RunState.Reported, RunState.Accepted) => true,
            (RunState.Reported, RunState.Rejected) => true,
            (RunState.Rejected, RunState.Released) => true,
            _ => false,
        };

    private static string Reason(RunState from, RunState to)
    {
        if (IsTerminal(from))
            return $"run is terminal ({from}); no outgoing transitions";
        if (to is RunState.Accepted)
            return $"cannot apply a run that is not Reported (run is {from})";
        if (to is RunState.Rejected)
            return $"cannot reject a run that is not Reported (run is {from})";
        if (from is RunState.Reported)
            return "a Reported run may only be Accepted or Rejected";
        return $"transition {from} -> {to} is not part of the verified-change lifecycle";
    }
}
