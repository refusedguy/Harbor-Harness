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
///     cell-diff engine. Documentation + grep anchor for the
///     <c>watch_*</c> partial wired next to the field; the wiring itself
///     stays an explicit <c>On&lt;Name&gt;Changed</c> partial (no reflection,
///     AOT-safe) that calls <see cref="ReactiveViewModel.MarkRepaint" /> or
///     <see cref="ReactiveViewModel.MarkLayout" />.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ReactiveAttribute : Attribute
{
    /// <summary>Cheap repaint: redraw cells in place (default).</summary>
    public bool Repaint { get; set; } = true;

    /// <summary>Expensive layout: recompute the widget tree (implies repaint).</summary>
    public bool Layout { get; set; }
}

/// <summary>
///     Base class for CellForge VMs with Textual-style reactive conventions
///     (steal/textual T2, issue #1184). NOT a TEA paradigm swap:
///     <c>UiState</c> + <c>ChatAppReducer</c> stay canonical; this only
///     structures per-VM field plumbing:
///     <list type="bullet">
///         <item><c>watch_*</c>: <c>On&lt;Name&gt;Changed</c> partials named
///         <c>Watch_&lt;Name&gt;</c> for side-effects (vs the
///         <c>UpdateFromEventAsync</c> pattern-match, which stays the event entry point).</item>
///         <item>layout-vs-repaint: <c>[Reactive(Layout = true)]</c> fields call
///         <see cref="MarkLayout" />, the rest call <see cref="MarkRepaint" />.</item>
///         <item><c>validate_*</c>: coerce/clamp helpers named
///         <c>Validate_&lt;Name&gt;</c>, applied in the changed-partial.</item>
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

    /// <summary>Shared <c>validate_*</c> helper: floors at zero.</summary>
    protected static int NonNegative(int value) => value < 0 ? 0 : value;

    /// <summary>Shared <c>validate_*</c> helper: nulls to empty.</summary>
    protected static string NonNull(string? value) => value ?? string.Empty;
}
