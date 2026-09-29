using System.Collections.ObjectModel;
using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using ToolCallViewModel = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;
using ChatLineViewModel = Harbor.Ui.Framework.ViewModels.ChatLineViewModel;

namespace Harbor.App.Avalonia.Services;

public sealed class UiRenderEngine
{
    private readonly DefaultUiProjector _projector;
    private readonly AvaloniaUiViewport _viewport;
    private readonly ChatStreamingPresenter _presenter;

    public UiRenderEngine(
        DefaultUiProjector projector,
        AvaloniaUiViewport viewport,
        ChatStreamingPresenter presenter)
    {
        _projector = projector;
        _viewport = viewport;
        _presenter = presenter;
    }

    internal void Render(UiState state, ChatViewModel vm)
    {
        var screen = _projector.Project(state);
        _viewport.SetCallbacks(
            v => vm.IsStreaming = v,
            v => vm.IsThinking = v,
            v => vm.IsAgentRunning = v,
            v => vm.StatusMessage = v,
            v => vm.StreamingBuffer = v,
            v => vm.InputText = v);
        _viewport.Apply(screen);

        ReconcileLines(state, vm);
        ReconcileToolCalls(state, vm);
        ReconcileTimeline(state, vm);
    }

    /// <summary>
    ///     Ф-A1b: project state.Chat.Lines into the single chronological
    ///     <see cref="ChatViewModelBase.Timeline" /> — chat rows and tool-call
    ///     cards interleaved in true order. Instances are REUSED positionally
    ///     (same role/text for chat lines, same tool id for cards) so a flush
    ///     during streaming only mutates the tail instead of rebuilding the
    ///     whole list and re-rendering every realized row. A call/result pair
    ///     collapses into ONE card (result preview lives on the card).
    /// </summary>
    private static void ReconcileTimeline(UiState state, ChatViewModel vm)
    {
        // Tool-card instances are final after ReconcileToolCalls; index by id.
        var toolById = new Dictionary<string, ToolCallViewModel>(StringComparer.Ordinal);
        for (int i = 0; i < vm.ToolCalls.Count; i++)
            toolById[vm.ToolCalls[i].Id] = vm.ToolCalls[i];

        var timeline = vm.Timeline;
        int written = 0;

        var consumed = new HashSet<ToolCallViewModel>();
        for (int i = 0; i < state.Chat.Lines.Length; i++)
        {
            var src = state.Chat.Lines[i];

            if (src.ToolCallId is not null &&
                toolById.TryGetValue(src.ToolCallId, out var card) &&
                consumed.Add(card))
            {
                PlaceAt(timeline, written++, card);
                continue;
            }

            if (src.ToolCallId is not null)
            {
                continue; // result line whose card is already placed
            }

            // Chat row: reuse the existing entry when role+text match.
            if (written < timeline.Count &&
                timeline[written] is ChatLineViewModel existingLine &&
                existingLine.Role == src.Role &&
                string.Equals(existingLine.Text, src.Text, StringComparison.Ordinal))
            {
                written++;
                continue;
            }

            PlaceAt(timeline, written++, new ChatLineViewModel(src.Role, src.Text));
        }

        for (int i = timeline.Count - 1; i >= written; i--)
            timeline.RemoveAt(i);
    }

    private static void PlaceAt(ObservableCollection<object> timeline, int index, object item)
    {
        if (index < timeline.Count)
        {
            if (!ReferenceEquals(timeline[index], item))
                timeline[index] = item;
        }
        else
        {
            timeline.Add(item);
        }
    }

    /// <summary>
    ///     Projects the state's tool calls onto the view-model's cards. Delegates
    ///     to the shared reconciler (#680): this used to be a second copy that
    ///     re-parsed the rendered transcript line and carried its own — different —
    ///     glyph table.
    /// </summary>
    private static void ReconcileToolCalls(UiState state, ChatViewModel vm)
        => ToolCallProjection.Reconcile(state, vm.ToolCalls);

    private static void ReconcileLines(UiState state, ChatViewModel vm)
    {
        var lines = vm.Lines;
        int n = state.Chat.Lines.Length;

        for (int i = 0; i < n; i++)
        {
            var src = state.Chat.Lines[i];
            if (i < lines.Count)
            {
                var cur = lines[i];
                if (cur.Role != src.Role || !string.Equals(cur.Text, src.Text, StringComparison.Ordinal))
                    lines[i] = new ChatLineViewModel(src.Role, src.Text);
            }
            else
            {
                lines.Add(new ChatLineViewModel(src.Role, src.Text));
            }
        }
        for (int i = lines.Count - 1; i >= n; i--)
            lines.RemoveAt(i);
    }

/// <summary>
    ///     The status the reducer decided for the active session — a read, not a
    ///     verdict. Kept as a method so <c>ChatViewModel.RenderFrameTick</c> has
    ///     one seam to call; see <c>ChatStreamingPresenter.DeriveStatus</c> for why
    ///     the answer is not computed here (#687).
    /// </summary>
    /// <param name="state">The current UiState.</param>
    /// <returns>The session's status.</returns>
    public SessionStatus DeriveStatus(UiState state)
    {
        return _presenter.DeriveStatus(state);
    }
}
