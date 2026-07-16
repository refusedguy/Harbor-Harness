using Harbor.Tui.Abstractions.Plugins;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
namespace Harbor.Tui.Abstractions.Views;
/// <summary>
///     Builtin status bar view — renders <see cref="StatusBarViewModel" /> state as a single
///     colored line showing model, agent, cost, tokens, and current status.
/// </summary>
/// <remarks>
///     <para>
///         This view is renderer-agnostic: it writes exclusively through
///         <see cref="ITuiRenderContext" />, never to <c>Console</c> directly. This means the same
///         view works under <c>AnsiTuiRenderer</c>, <c>PlainTuiRenderer</c>,
///         <c>SpectreTuiRenderer</c>, and the <c>CaptureRenderContext</c> used in tests.
///     </para>
///     <para>
///         Plugins can replace this view by registering a custom <see cref="ITuiView" /> with the
///         same id (<c>"status-bar"</c>) before <see cref="BaseTuiRenderer.InitializeAsync" /> is
///         called — see <see cref="ITuiPlugin" />.
///     </para>
/// </remarks>
public sealed class StatusBarView : TuiViewBase<StatusBarViewModel>
{
    /// <inheritdoc />
    public override string Id => "status-bar";

    /// <inheritdoc />
    public override string DisplayName => "Status Bar";

    /// <inheritdoc />
    public override TuiViewPlacement Placement => TuiViewPlacement.StatusBar;

    /// <inheritdoc />
    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
    {
        var vm = this.ViewModel;
        if (vm is null)
        {
            return Task.CompletedTask;
        }

        // Clear the line so the status bar can be repainted in place (full-screen TUIs).
        context.ClearLine();

        if (context.SupportsColor)
        {
            var color = vm.Status switch
            {
                "running" => TuiColor.Cyan,
                "error" => TuiColor.Red,
                "compacting" => TuiColor.Yellow,
                _ => TuiColor.Gray
            };
            context.WriteColored(vm.Formatted, color);
        }
        else
        {
            context.Write(vm.Formatted);
        }

        context.WriteLine();
        return Task.CompletedTask;
    }
}
