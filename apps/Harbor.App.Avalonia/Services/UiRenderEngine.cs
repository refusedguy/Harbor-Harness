using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using ToolCallStatus = Harbor.Ui.Framework.ViewModels.ToolCallStatus;
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
    ///     Ф-A1b: project state.Lines into the single chronological
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
        for (int i = 0; i < state.Lines.Length; i++)
        {
            var src = state.Lines[i];

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

    private static void ReconcileToolCalls(UiState state, ChatViewModel vm)
    {
        var existingById = new Dictionary<string, ToolCallViewModel>(StringComparer.Ordinal);
        for (int i = 0; i < vm.ToolCalls.Count; i++)
            existingById[vm.ToolCalls[i].Id] = vm.ToolCalls[i];

        var ordered = new List<ToolCallViewModel>(state.Lines.Length);

        for (int i = 0; i < state.Lines.Length; i++)
        {
            var line = state.Lines[i];
            if (line.ToolCallId is null) continue;

            if (line.Role == ChatRole.Tool)
            {
                var parsed = ParseToolLine(line.Text, line.ToolCallId);
                if (parsed is null) continue;

                if (existingById.TryGetValue(parsed.Id, out var existing))
                {
                    existing.ToolName = parsed.ToolName;
                    existing.ArgsPreview = parsed.ArgsPreview;
                    existing.IconText = parsed.IconText;
                    if (parsed.IsDiffTool)
                    {
                        existing.IsDiffTool = true;
                        existing.DiffFilePath = parsed.DiffFilePath;
                        existing.DiffPreview = parsed.DiffPreview;
                        existing.DiffFull = parsed.DiffFull;
                    }
                    ordered.Add(existing);
                }
                else
                {
                    ordered.Add(parsed);
                }
            }
            else if (line.Role == ChatRole.ToolResult && existingById.TryGetValue(line.ToolCallId!, out var entry))
            {
                entry.Complete(
                    line.Text.StartsWith("✗", StringComparison.Ordinal) ? ToolCallStatus.Error : ToolCallStatus.Success,
                    FormatResultPreview(line.Text),
                    TimeSpan.Zero);
                ordered.Add(entry);
            }
        }

        int currentCount = vm.ToolCalls.Count;
        int targetCount = ordered.Count;

        if (currentCount != targetCount)
        {
            vm.ToolCalls.Clear();
            foreach (var tc in ordered)
                vm.ToolCalls.Add(tc);
            return;
        }

        for (int i = 0; i < currentCount; i++)
        {
            if (vm.ToolCalls[i] != ordered[i])
                vm.ToolCalls[i] = ordered[i];
        }
    }

    private static void ReconcileLines(UiState state, ChatViewModel vm)
    {
        var lines = vm.Lines;
        int n = state.Lines.Length;

        for (int i = 0; i < n; i++)
        {
            var src = state.Lines[i];
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

    private static ToolCallViewModel? ParseToolLine(string text, string toolCallId)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2 || text[0] != '→')
            return null;

        int spaceIdx = text.IndexOf(' ', 2);
        string toolName;
        string argsJson;
        if (spaceIdx < 0)
        {
            toolName = text[2..];
            argsJson = "{}";
        }
        else
        {
            toolName = text[2..spaceIdx];
            argsJson = text[(spaceIdx + 1)..].TrimStart();
        }

        var vm = new ToolCallViewModel
        {
            Id = toolCallId,
            ToolName = toolName,
            ArgsPreview = argsJson == "{}" ? string.Empty : argsJson,
            IconText = toolName switch
            {
                "edit" => "✎",
                "write" => "✚",
                "read" => "▸",
                "patch" => "⌥",
                "bash" => "$",
                "grep" => "🔍",
                "glob" => "🌐",
                "ls" => "📁",
                "task" => "☐",
                "web_fetch" => "🌍",
                _ => "?"
            }
        };

        var diffData = DiffPreviewHelper.ExtractDiff(toolName, argsJson, null);
        if (diffData.IsDiffTool)
        {
            vm.IsDiffTool = true;
            vm.DiffFilePath = diffData.FilePath;
            vm.DiffPreview = diffData.Preview;
            vm.DiffFull = diffData.FullDiff;
        }

        return vm;
    }

    private static string FormatResultPreview(string resultText)
    {
        if (string.IsNullOrEmpty(resultText))
            return string.Empty;
        if (resultText.Length >= 2 && (resultText[0] == '✓' || resultText[0] == '✗') && resultText[1] == ' ')
            return resultText[2..];
        return resultText;
    }

    public SessionStatus DeriveStatus(UiState state)
    {
        return _presenter.DeriveStatus(state);
    }
}
