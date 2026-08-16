using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Animation;

namespace Harbor.App.Avalonia.Services;

internal sealed class AvaloniaWorkspaceCommands : IWorkspaceCommands
{
    private readonly ChatViewModel _chat;
    private readonly SessionListViewModel _sessions;
    private readonly CodeEditorViewModel _codeEditor;
    private readonly TuiEffectHost _effects;

    public AvaloniaWorkspaceCommands(
        ChatViewModel chat,
        SessionListViewModel sessions,
        CodeEditorViewModel codeEditor,
        TuiEffectHost effects)
    {
        _chat = chat;
        _sessions = sessions;
        _codeEditor = codeEditor;
        _effects = effects;
    }

    public void NewSession() => _sessions.NewSessionCommand.Execute(null);
    public void BranchSession() => _sessions.BranchCommand.ExecuteAsync(null);
    public void RefreshSessions() => _sessions.RefreshCommand.ExecuteAsync(null);
    public async System.Threading.Tasks.Task OpenFileAsync() => await _codeEditor.OpenFileCommand.ExecuteAsync(null);
    public async System.Threading.Tasks.Task SaveFileAsync() => await _codeEditor.SaveCommand.ExecuteAsync(null);
    public void StopAgent() => _chat.StopCommand.Execute(null);
    public void ClearChat() => _chat.ClearCommand.Execute(null);
}
