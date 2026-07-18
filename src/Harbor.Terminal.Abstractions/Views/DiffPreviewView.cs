using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
namespace Harbor.Terminal.Abstractions.Views;
/// <summary>
///     Builtin diff preview view — renders <see cref="DiffPreviewViewModel" /> state: the
///     currently selected <see cref="DiffEntry" /> with a "Diff N of M" header and navigation
///     hints, rendered as an overlay panel.
/// </summary>
/// <remarks>
///     <para>
///         When the view model has no diffs, the view renders nothing (early return). This keeps
///         the overlay invisible until the first file change is recorded by
///         <see cref="DiffPreviewViewModel.UpdateFromEventAsync" />.
///     </para>
///     <para>
///         This view is renderer-agnostic and writes only through <see cref="ITuiRenderContext" />.
///     </para>
/// </remarks>
public sealed class DiffPreviewView : TuiViewBase<DiffPreviewViewModel>
{
    /// <inheritdoc />
    public override string Id => "diff-preview";

    /// <inheritdoc />
    public override string DisplayName => "Diff Preview";

    /// <inheritdoc />
    public override TuiViewPlacement Placement => TuiViewPlacement.Overlay;

    /// <inheritdoc />
    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
    {
        var vm = this.ViewModel;
        if (vm is null || vm.Diffs.Count == 0)
        {
            return Task.CompletedTask;
        }

        context.WriteLine();
        string header = $"── Diff {vm.CurrentIndex + 1} of {vm.Diffs.Count} ──";
        if (context.SupportsColor)
        {
            context.WriteColored(header, TuiColor.Yellow);
        }
        else
        {
            context.Write(header);
        }
        context.WriteLine();

        var current = vm.Current;
        if (current is not null)
        {
            if (context.SupportsColor)
            {
                context.WriteColored($"[{current.ToolName}]", TuiColor.Magenta);
            }
            else
            {
                context.Write($"[{current.ToolName}]");
            }
            context.WriteLine();
            context.WriteLine(current.Output);
        }

        if (vm.Diffs.Count > 1)
        {
            string hint = "  (n: next, p: previous)";
            if (context.SupportsColor)
            {
                context.WriteStyled(hint, TuiStyle.Dim);
            }
            else
            {
                context.Write(hint);
            }
            context.WriteLine();
        }

        return Task.CompletedTask;
    }
}
