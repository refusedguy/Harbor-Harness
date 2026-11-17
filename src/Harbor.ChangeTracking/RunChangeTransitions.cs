namespace Harbor.ChangeTracking;

/// <summary>
///     Behaviour of one state of the verified-change run lifecycle
///     (<see cref="RunState" />; epic #42, slice S9, #397): the states this
///     one may move to and the reason it refuses every other target (#1484).
///     A state owns its own transitions and its own refusal text, so the three
///     old parallel sources of truth — the central <c>(from, to)</c> switch,
///     the <c>Reason</c> if-chain and <c>IsTerminal</c> — are one object per
///     value. Adding a lifecycle state means adding one implementation and
///     registering it in a <see cref="RunTransitionTable" />; no existing
///     state has to change.
/// </summary>
public interface IRunState
{
    /// <summary>The enum value this behaviour answers for.</summary>
    RunState Value { get; }

    /// <summary>Targets this state allows. Empty means terminal.</summary>
    IReadOnlyCollection<RunState> LegalTargets { get; }

    /// <summary>True when no outgoing transition is legal from this state.</summary>
    bool IsTerminal { get; }

    /// <summary>Why moving to <paramref name="to" /> is refused.</summary>
    string RefusalReason(RunState to);
}

/// <summary>
///     The shared refusal wording for every non-terminal state. Only
///     <see cref="ReportedRunState" /> (its own wording) and the unregistered
///     fallback override it.
/// </summary>
internal abstract class RunStateBase : IRunState
{
    protected RunStateBase(RunState value, params RunState[] legalTargets)
    {
        Value = value;
        LegalTargets = legalTargets;
    }

    public RunState Value { get; }

    public IReadOnlyCollection<RunState> LegalTargets { get; }

    public virtual bool IsTerminal => LegalTargets.Count == 0;

    public virtual string RefusalReason(RunState to)
    {
        if (IsTerminal)
            return $"run is terminal ({Value}); no outgoing transitions";
        if (to is RunState.Accepted)
            return $"cannot apply a run that is not Reported (run is {Value})";
        if (to is RunState.Rejected)
            return $"cannot reject a run that is not Reported (run is {Value})";
        return $"transition {Value} -> {to} is not part of the verified-change lifecycle";
    }
}

internal sealed class PinnedRunState : RunStateBase
{
    public PinnedRunState() : base(RunState.Pinned, RunState.Isolated) { }
}

internal sealed class IsolatedRunState : RunStateBase
{
    public IsolatedRunState() : base(RunState.Isolated, RunState.Changed, RunState.Released) { }
}

internal sealed class ChangedRunState : RunStateBase
{
    public ChangedRunState() : base(RunState.Changed, RunState.Checked) { }
}

internal sealed class CheckedRunState : RunStateBase
{
    public CheckedRunState() : base(RunState.Checked, RunState.Reported) { }
}

internal sealed class ReportedRunState : RunStateBase
{
    public ReportedRunState() : base(RunState.Reported, RunState.Accepted, RunState.Rejected) { }

    public override string RefusalReason(RunState to) =>
        "a Reported run may only be Accepted or Rejected";
}

internal sealed class AcceptedRunState : RunStateBase
{
    public AcceptedRunState() : base(RunState.Accepted) { }
}

internal sealed class RejectedRunState : RunStateBase
{
    public RejectedRunState() : base(RunState.Rejected, RunState.Released) { }
}

internal sealed class ReleasedRunState : RunStateBase
{
    public ReleasedRunState() : base(RunState.Released) { }
}

/// <summary>
///     Refusal behaviour for a <see cref="RunState" /> value that has no
///     registered state object. It is fail-closed like the original default
///     switch arm: never terminal, no legal targets, the generic reason.
/// </summary>
internal sealed record UnregisteredRunState(RunState Value) : IRunState
{
    public IReadOnlyCollection<RunState> LegalTargets => Array.Empty<RunState>();

    public bool IsTerminal => false;

    public string RefusalReason(RunState to) =>
        $"transition {Value} -> {to} is not part of the verified-change lifecycle";
}

/// <summary>
///     The registry of state behaviours and the single place a transition is
///     decided (#1484). Built once from an explicit list of state objects;
///     a caller that needs a different set (a probe state, a future lifecycle
///     state) constructs its own table instead of mutating the shared one.
///     Every verdict comes from the <c>from</c> state object: its legal
///     targets decide the verdict, its refusal reason names the failure, and
///     its terminal flag classifies the state.
/// </summary>
public sealed class RunTransitionTable
{
    private readonly IReadOnlyDictionary<RunState, IRunState> _states;

    public RunTransitionTable(IEnumerable<IRunState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _states = states.ToDictionary(state => state.Value);
    }

    /// <summary>The registered state behaviours, for callers that extend the table.</summary>
    public IEnumerable<IRunState> States => _states.Values;

    /// <summary>The eight built-in lifecycle states, in chain order.</summary>
    public static RunTransitionTable CreateDefault() => new(
    [
        new PinnedRunState(),
        new IsolatedRunState(),
        new ChangedRunState(),
        new CheckedRunState(),
        new ReportedRunState(),
        new AcceptedRunState(),
        new RejectedRunState(),
        new ReleasedRunState(),
    ]);

    /// <summary>True when <paramref name="state" /> has no outgoing transitions.</summary>
    public bool IsTerminal(RunState state) =>
        _states.TryGetValue(state, out IRunState? value) && value.IsTerminal;

    /// <summary>
    ///     Validate one lifecycle step. Success carries the target state so
    ///     callers can persist exactly what was validated; failure is
    ///     fail-closed with the named reason from the source state.
    /// </summary>
    public Result<RunState> TryTransition(RunState from, RunState to)
    {
        IRunState state = _states.TryGetValue(from, out IRunState? value)
            ? value
            : new UnregisteredRunState(from);
        if (state.LegalTargets.Contains(to))
            return Result.Success(to);
        return Result.Failure<RunState>(
            $"Illegal run transition {from} -> {to}: {state.RefusalReason(to)}.");
    }
}

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
/// <remarks>
///     #1484: the verdict, the refusal reason and the terminal classification
///     now come from the per-state objects in <see cref="RunTransitionTable" />.
///     The public surface below is unchanged; every caller is untouched.
/// </remarks>
public static class RunChangeTransitions
{
    private static readonly RunTransitionTable Default = RunTransitionTable.CreateDefault();

    /// <summary>
    ///     Terminal states: no outgoing transition is legal from them.
    /// </summary>
    public static bool IsTerminal(RunState state) => Default.IsTerminal(state);

    /// <summary>
    ///     Validate one lifecycle step. Success carries the target state so
    ///     callers can persist exactly what was validated.
    /// </summary>
    public static Result<RunState> TryTransition(RunState from, RunState to) =>
        Default.TryTransition(from, to);
}
