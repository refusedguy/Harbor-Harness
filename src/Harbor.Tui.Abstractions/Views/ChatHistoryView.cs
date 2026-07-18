using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
namespace Harbor.Tui.Abstractions.Views;
/// <summary>
///     Builtin chat history view — renders <see cref="ChatHistoryViewModel" /> state: accumulated
///     <see cref="ChatEntry" /> records plus optional live streaming text and thinking buffer.
/// </summary>
/// <remarks>
///     <para>
///         Each entry is rendered with a role-colored prefix (<c>[user]</c>, <c>[assistant]</c>,
///         <c>[tool]</c>, <c>[result]</c>). When <see cref="ChatHistoryViewModel.IsStreaming" /> is
///         active, the in-progress <see cref="ChatHistoryViewModel.StreamingText" /> is appended as a
///         trailing assistant entry. When <see cref="ChatHistoryViewModel.IsThinking" /> is active,
///         the thinking buffer is rendered dimmed/italic so users can follow reasoning.
///     </para>
///     <para>
///         This view is renderer-agnostic and writes only through <see cref="ITuiRenderContext" />.
///     </para>
/// </remarks>
public sealed class ChatHistoryView : TuiViewBase<ChatHistoryViewModel>
{
    /// <inheritdoc />
    public override string Id => "chat-history";

    /// <inheritdoc />
    public override string DisplayName => "Chat History";

    /// <inheritdoc />
    public override TuiViewPlacement Placement => TuiViewPlacement.ChatHistory;

    /// <inheritdoc />
    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
    {
        var vm = this.ViewModel;
        if (vm is null)
        {
            return Task.CompletedTask;
        }

        foreach (var entry in vm.Entries)
        {
            RenderEntry(context, entry.Role, entry.Content);
        }

        // Render the live streaming text (if any) as a trailing assistant entry. This is
        // primarily useful in full-screen TUIs that repaint the whole pane; streaming
        // renderers (Ansi/Plain) typically emit tokens directly and skip placement-driven
        // repaints for MessageUpdateEvent.
        if (vm.IsStreaming && !string.IsNullOrEmpty(vm.StreamingText))
        {
            RenderEntry(context, "assistant", vm.StreamingText);
        }

        if (vm.IsThinking && !string.IsNullOrEmpty(vm.ThinkingText))
        {
            if (context.SupportsColor)
            {
                context.WriteStyled($"[thinking] {vm.ThinkingText}", TuiStyle.Dim | TuiStyle.Italic);
            }
            else
            {
                context.Write($"[thinking] {vm.ThinkingText}");
            }
            context.WriteLine();
        }

        return Task.CompletedTask;
    }

    private static void RenderEntry(ITuiRenderContext context, string role, string content)
    {
        string prefix = role switch
        {
            "user" => "[user] ",
            "assistant" => "[assistant] ",
            "tool" => "[tool] ",
            "tool-result" => "[result] ",
            "system" => "",
            "error" => "[error] ",
            _ => $"[{role}] "
        };

        if (context.SupportsColor)
        {
            var color = role switch
            {
                "user" => TuiColor.Green,
                "assistant" => TuiColor.Cyan,
                "tool" => TuiColor.Blue,
                "tool-result" => TuiColor.Gray,
                "system" => TuiColor.Yellow,
                "error" => TuiColor.Red,
                _ => TuiColor.Default
            };
            context.WriteColored(prefix, color);
        }
        else
        {
            context.Write(prefix);
        }

        context.WriteLine(content);
    }
}
