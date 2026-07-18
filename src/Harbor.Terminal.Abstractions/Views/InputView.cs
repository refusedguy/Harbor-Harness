using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
namespace Harbor.Terminal.Abstractions.Views;
/// <summary>
///     Builtin input view — renders <see cref="InputViewModel" /> state: the user prompt line
///     with a green <c>&gt;</c> prefix, a dim placeholder when empty, and a reverse-video
///     cursor block at <see cref="InputViewModel.CursorPosition" />.
/// </summary>
/// <remarks>
///     <para>
///         The view is stateless: it draws the current text + cursor snapshot every time it is
///         rendered. Full-screen TUIs repaint it on each key press; streaming renderers can opt
///         out of placement-driven Input repaints and rely on their own readline loop.
///     </para>
///     <para>
///         This view is renderer-agnostic and writes only through <see cref="ITuiRenderContext" />.
///     </para>
/// </remarks>
public sealed class InputView : TuiViewBase<InputViewModel>
{
    private const string PromptPrefix = "> ";

    /// <inheritdoc />
    public override string Id => "input";

    /// <inheritdoc />
    public override string DisplayName => "Input";

    /// <inheritdoc />
    public override TuiViewPlacement Placement => TuiViewPlacement.Input;

    /// <inheritdoc />
    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
    {
        var vm = this.ViewModel;
        if (vm is null)
        {
            return Task.CompletedTask;
        }

        context.ClearLine();

        if (context.SupportsColor)
        {
            context.WriteColored(PromptPrefix, TuiColor.Green);
        }
        else
        {
            context.Write(PromptPrefix);
        }

        if (string.IsNullOrEmpty(vm.Text))
        {
            if (context.SupportsColor)
            {
                context.WriteStyled(vm.Placeholder, TuiStyle.Dim);
            }
            else
            {
                context.Write(vm.Placeholder);
            }
        }
        else
        {
            RenderTextWithCursor(context, vm.Text, vm.CursorPosition);
        }

        context.WriteLine();
        return Task.CompletedTask;
    }

    private static void RenderTextWithCursor(ITuiRenderContext context, string text, int cursor)
    {
        int pos = Math.Clamp(cursor, 0, text.Length);
        string before = text[..pos];
        string atCursor = pos < text.Length ? text[pos].ToString() : " ";
        string after = pos < text.Length ? text[(pos + 1)..] : string.Empty;

        context.Write(before);
        if (context.SupportsColor)
        {
            context.WriteStyled(atCursor, TuiStyle.Reverse);
        }
        else
        {
            // Plain context: represent the cursor with an underscore block.
            context.Write(atCursor == " " ? "_" : atCursor);
        }
        context.Write(after);
    }
}
