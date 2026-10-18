using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Events;

namespace Harbor.Terminal.Abstractions.ViewModels;

/// <summary>
///     Dirty kind for the cell-diff engine (steal/textual T2, issue #1184).
///     Mirrors <c>Reactive._layout</c> / <c>_repaint</c> in Textual's
///     <c>reactive.py</c>: a cheap repaint redraws cells in place, an
///     expensive layout recomputes the widget tree.
/// </summary>
public enum ReactiveDirty
{
    Clean,
    Repaint,
    Layout,
}

/// <summary>
///     Declares how a <c>[ObservableProperty]</c> field invalidates the
///     cell-diff engine. Read by <c>ReactiveHookGenerator</c> (Harbor.CodeGen),
///     which emits the <c>On&lt;Name&gt;Changed</c> hook for every
///     <c>[Reactive]</c> field whose containing class derives
///     <see cref="ReactiveViewModel" /> (issue #1542): the
///     MarkRepaint/MarkLayout call follows <see cref="Repaint" /> /
///     <see cref="Layout" />, and the optional coerce branch re-assigns the
///     coerced value through the generated property (the setter re-enters
///     the hook and marks the dirty kind there).
///
///     This is the single record of intent — do NOT also write a manual
///     <c>On&lt;Name&gt;Changed</c> for a <c>[Reactive]</c> field: a second
///     implementation of the same partial method stops the build (two
///     records of one thing, and it would double-fire). Manual
///     <c>On&lt;Name&gt;Changed</c> hooks remain available only for fields
///     tagged but NOT marked <c>[Reactive]</c> (pure side-effect
///     <c>watch_*</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ReactiveAttribute : Attribute
{
    /// <summary>Cheap repaint: redraw cells in place (default).</summary>
    public bool Repaint { get; set; } = true;

    /// <summary>Expensive layout: recompute the widget tree (implies repaint).</summary>
    public bool Layout { get; set; }

    /// <summary>
    ///     Generated coerce branch: floors at zero (issue #1542). A set below
    ///     0 is re-assigned as 0 through the generated property; the setter
    ///     re-enters the hook and marks the dirty kind. Only for int-typed
    ///     fields — anything else fails to compile (type mismatch).
    /// </summary>
    public bool NonNegative { get; set; }

    /// <summary>
    ///     Generated coerce branch: <see langword="null" /> becomes
    ///     <see cref="string.Empty" /> (issue #1542). Only for
    ///     string-typed fields.
    /// </summary>
    public bool NonNull { get; set; }
}

/// <summary>
///     Base class for CellForge VMs with Textual-style reactive conventions
///     (steal/textual T2, issue #1184). NOT a TEA paradigm swap:
///     <c>UiState</c> + <c>ChatAppReducer</c> stay canonical; this only
///     structures per-VM field plumbing:
///     <list type="bullet">
///         <item>layout-vs-repaint + coerce: <c>[Reactive]</c> options drive
///         the GENERATED <c>On&lt;Name&gt;Changed</c> hook
///         (<c>ReactiveHookGenerator</c>, issue #1542) — see
///         <see cref="ReactiveAttribute" />. Manual <c>On&lt;Name&gt;Changed</c>
///         implementations for <c>[Reactive]</c> fields collide with the
///         generated ones and stop the build; side-effect hooks stay
///         hand-written only for fields NOT carrying <c>[Reactive]</c>.</item>
///         <item><c>validate_*</c>: the coerce helpers
///         <see cref="NonNegative" /> / <see cref="NonNull" /> are shared
///         statics the generated coerce branch calls — not per-field shims.</item>
///         <item><c>compute_*</c>: read-only derived getters (no manual
///         two-field sync).</item>
///     </list>
/// </summary>
public abstract partial class ReactiveViewModel : ObservableObject, ITuiViewModel
{
    private bool _needsLayout;
    private bool _needsRepaint;

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <summary>An expensive layout recompute was requested.</summary>
    public bool NeedsLayout
    {
        get => _needsLayout;
        private set => SetProperty(ref _needsLayout, value);
    }

    /// <summary>A cheap in-place repaint was requested.</summary>
    public bool NeedsRepaint
    {
        get => _needsRepaint;
        private set => SetProperty(ref _needsRepaint, value);
    }

    /// <summary>Any invalidation is pending.</summary>
    public bool IsDirty => NeedsLayout || NeedsRepaint;

    /// <inheritdoc />
    public virtual Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Mark a cheap repaint (<see cref="ReactiveDirty.Repaint" />).</summary>
    protected void MarkRepaint() => NeedsRepaint = true;

    /// <summary>
    ///     Mark an expensive layout recompute. Implies repaint: a relaid-out
    ///     tree always needs a redraw.
    /// </summary>
    protected void MarkLayout()
    {
        NeedsLayout = true;
        NeedsRepaint = true;
    }

    /// <summary>Mark the given dirty kind.</summary>
    protected void MarkDirty(ReactiveDirty kind)
    {
        switch (kind)
        {
            case ReactiveDirty.Repaint:
                MarkRepaint();
                break;
            case ReactiveDirty.Layout:
                MarkLayout();
                break;
        }
    }

    /// <summary>
    ///     Consume pending invalidation after the renderer has drawn.
    /// </summary>
    public void ClearDirty()
    {
        NeedsLayout = false;
        NeedsRepaint = false;
    }

    /// <summary>Shared coerce helper: floors at zero (called by the generated hook, issue #1542).</summary>
    protected static int NonNegative(int value) => value < 0 ? 0 : value;

    /// <summary>Shared coerce helper: nulls to empty (called by the generated hook, issue #1542).</summary>
    protected static string NonNull(string? value) => value ?? string.Empty;
}
