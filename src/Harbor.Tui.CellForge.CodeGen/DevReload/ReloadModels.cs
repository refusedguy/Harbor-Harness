namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     What one dev reload step did to the live element tree. Hot outcomes
///     keep the tree (and everything keyed by element id) alive; cold outcomes
///     replace it, and <see cref="RestartRequired"/> also asks the host to
///     restart because binding or handler logic changed.
/// </summary>
public enum ReloadOutcome
{
    /// <summary>The new source is identical after comment stripping and normalization.</summary>
    Unchanged,

    /// <summary>Values changed inside an unchanged template; the tree was patched in place.</summary>
    Patched,

    /// <summary>The template shape changed; the tree was rebuilt in place.</summary>
    Rebuilt,

    /// <summary>Logic changed; the tree was rebuilt and the host must restart.</summary>
    RestartRequired,

    /// <summary>The new source did not parse; the live tree was left untouched.</summary>
    Rejected
}

/// <summary>
///     Host state attached to a stable element id. The host normally keeps its
///     real view-model, focus and scroll state in the TEA store; this record is
///     the dev-facing view of that state, keyed by the same id so a patch can
///     prove it survived.
/// </summary>
/// <param name="ViewModelState">Opaque view-model snapshot, if the host keeps one.</param>
/// <param name="Focused">Whether this element owns keyboard focus.</param>
/// <param name="ScrollOffset">Scroll position in rows, in element-local coordinates.</param>
public sealed record ElementSessionState(string? ViewModelState, bool Focused, int ScrollOffset);

/// <summary>The deterministic result of one <see cref="DevReloadInterpreter.Reload"/> call.</summary>
/// <param name="Outcome">What happened to the live tree.</param>
/// <param name="Revision">Tree revision after the step.</param>
/// <param name="LiveRootId">Root element id after the step; zero when the tree is empty.</param>
/// <param name="PreservedElementIds">
///     Element ids whose session state survived the step. Empty on a rebuild or restart.
/// </param>
/// <param name="TemplateSignature">The emptied-tree signature used for the patch/rebuild decision.</param>
/// <param name="SourceChecksum">SHA-256 of the source text, matching the generated <c>SourceChecksum</c>.</param>
/// <param name="Reason">Short, human-readable explanation of the decision.</param>
public sealed record ReloadResult(
    ReloadOutcome Outcome,
    int Revision,
    int LiveRootId,
    IReadOnlyList<int> PreservedElementIds,
    string TemplateSignature,
    string SourceChecksum,
    string Reason)
{
    /// <summary>True when the live tree was kept and only values were updated.</summary>
    public bool HotApplied => Outcome is ReloadOutcome.Unchanged or ReloadOutcome.Patched;

    /// <summary>True when the host must restart to pick up new logic.</summary>
    public bool NeedsRestart => Outcome == ReloadOutcome.RestartRequired;
}
