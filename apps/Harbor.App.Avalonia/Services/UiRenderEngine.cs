using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using ToolCallStatus = Harbor.Ui.Framework.ViewModels.ToolCallStatus;

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
    }

    private static void ReconcileToolCalls(UiState state, ChatViewModel vm)
    {
        var toolCalls = new Dictionary<string, (int Index, ToolCallViewModel Vm)>(StringComparer.Ordinal);

        for (int i = 0; i < state.Lines.Length; i++)
        {
            var line = state.Lines[i];
            if (line.ToolCallId is null) continue;

            if (line.Role == ChatRole.Tool)
            {
                var parsed = ParseToolLine(line.Text, line.ToolCallId);
                if (parsed is not null)
                {
                    if (toolCalls.TryGetValue(line.ToolCallId, out var existing))
                    {
                        existing.Vm.ToolName = parsed.ToolName;
                        existing.Vm.ArgsPreview = parsed.ArgsPreview;
                        existing.Vm.IconText = parsed.IconText;
                        if (parsed.IsDiffTool)
                        {
                            existing.Vm.IsDiffTool = true;
                            existing.Vm.DiffFilePath = parsed.DiffFilePath;
                            existing.Vm.DiffPreview = parsed.DiffPreview;
                            existing.Vm.DiffFull = parsed.DiffFull;
                        }
                    }
                    else
                    {
                        toolCalls[line.ToolCallId] = (i, parsed);
                    }
                }
            }
            else if (line.Role == ChatRole.ToolResult && toolCalls.TryGetValue(line.ToolCallId, out var entry))
            {
                entry.Vm.Complete(
                    line.Text.StartsWith("✗", StringComparison.Ordinal) ? ToolCallStatus.Error : ToolCallStatus.Success,
                    FormatResultPreview(line.Text),
                    TimeSpan.Zero);
            }
        }

        var ordered = toolCalls.Values.OrderBy(t => t.Index).Select(t => t.Vm).ToList();

        if (vm.ToolCalls.Count != ordered.Count || !vm.ToolCalls.SequenceEqual(ordered, new ToolCallViewModelComparer()))
        {
            vm.ToolCalls.Clear();
            foreach (var tc in ordered)
                vm.ToolCalls.Add(tc);
        }
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

    private static void ReconcileLines(UiState state, ChatViewModel vm)
    {
        var newLines = ImmutableArray.CreateBuilder<ChatLineViewModel>();
        foreach (var line in state.Lines)
        {
            newLines.Add(new ChatLineViewModel(line.Role, line.Text));
        }

        var newArray = newLines.ToImmutable();
        if (vm.Lines.Count != newArray.Length || !vm.Lines.SequenceEqual(newArray, new ChatLineViewModelComparer()))
        {
            vm.Lines.Clear();
            foreach (var line in newArray)
                vm.Lines.Add(line);
        }
    }
}

file sealed class ToolCallViewModelComparer : IEqualityComparer<ToolCallViewModel>
{
    public bool Equals(ToolCallViewModel? x, ToolCallViewModel? y)
    {
        if (x is null || y is null) return x is null && y is null;
        return x.Id == y.Id && x.ToolName == y.ToolName && x.Status == y.Status
            && x.ArgsPreview == y.ArgsPreview && x.ResultPreview == y.ResultPreview
            && x.IsDiffTool == y.IsDiffTool && x.DiffFilePath == y.DiffFilePath
            && x.DiffPreview == y.DiffPreview && x.DiffFull == y.DiffFull;
    }

    public int GetHashCode(ToolCallViewModel obj)
    {
        return HashCode.Combine(obj.Id, obj.ToolName, obj.Status, obj.ArgsPreview, obj.ResultPreview);
    }
}

file sealed class ChatLineViewModelComparer : IEqualityComparer<ChatLineViewModel>
{
    public bool Equals(ChatLineViewModel? x, ChatLineViewModel? y)
    {
        if (x is null || y is null) return x is null && y is null;
        return x.Role == y.Role && x.Text == y.Text;
    }

    public int GetHashCode(ChatLineViewModel obj)
    {
        return HashCode.Combine(obj.Role, obj.Text);
    }
}