using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;

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

    public void Render(UiState state, ChatViewModel vm)
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

    public SessionStatus DeriveStatus(UiState state)
    {
        return _presenter.DeriveStatus(state);
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