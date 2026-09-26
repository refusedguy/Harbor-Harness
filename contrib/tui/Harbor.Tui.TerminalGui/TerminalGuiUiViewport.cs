using Harbor.Tui.TerminalGui.Rendering;
using Harbor.Tui.TerminalGui.Views;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Terminal.Gui;
using Terminal.Gui.Views;

namespace Harbor.Tui.TerminalGui;

internal sealed class TerminalGuiUiViewport : IUiViewport
{
    private readonly ChatView _chatView;
    private readonly StatusBarView _statusBarView;
    private readonly TextView _output;
    private readonly Label _header;
    private readonly Label _statusBar;
    private readonly TextView _input;

    public TerminalGuiUiViewport(
        ChatView chatView,
        StatusBarView statusBarView,
        TextView output,
        Label header,
        Label statusBar,
        TextView input)
    {
        _chatView = chatView;
        _statusBarView = statusBarView;
        _output = output;
        _header = header;
        _statusBar = statusBar;
        _input = input;
    }

    public void Apply(UiScreenModel screen)
    {
        var lines = DefaultUiProjector.ExtractRenderedLines(screen);
        var texts = lines.Select(l => string.Join(string.Empty, l.Spans.Select(s => s.Text)));
        _output.Text = string.Join('\n', texts);

        if (screen.Header.IsAgentRunning && screen.Transcript.StreamingBlockId != null)
            _output.MoveEnd();

        _header.Text = $" ⚓ {screen.Header.Provider}/{screen.Header.Model} | {screen.Header.AgentName}  |  Enter: Send  |  /help: Commands  |  F12: Logs";

        _statusBar.Text = " " + _statusBarView.Build(screen);

        _input.Text = screen.Input.Text;
    }
}