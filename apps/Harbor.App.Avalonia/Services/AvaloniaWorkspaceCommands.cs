using Harbor.App.Avalonia.ViewModels;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Animation;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Avalonia binding of <see cref="IWorkspaceCommands" /> onto the shell's
///     view-models.
/// </summary>
/// <remarks>
///     <para>
///         <b>The worst shape in the shell before #569.</b> Two members read
///         <c>public void BranchSession() =&gt; _sessions.BranchCommand.ExecuteAsync(null);</c>
///         — a <c>void</c> method whose body IS a Task-returning call. The
///         compiler accepts it without a word, there is no <c>_ = </c> marker to
///         grep for, and the Task is dropped on the floor. That is why the
///         original audit, which searched for <c>_ = SomeAsync()</c>, missed
///         these two while catching the far more visible sites next door.
///     </para>
///     <para>
///         <see cref="IWorkspaceCommands" /> declares the sync members as
///         <c>void</c> because the palette's command table and the key handler
///         are synchronous by contract. The contract is not the defect; losing
///         the fault is. Both now route through
///         <see cref="TaskFireAndForget" />, which observes the Task and reports
///         the fault instead of losing it at finalization.
///     </para>
/// </remarks>
internal sealed class AvaloniaWorkspaceCommands : IWorkspaceCommands
{
    private readonly ChatViewModel _chat;
    private readonly SessionListViewModel _sessions;
    private readonly CodeEditorViewModel _codeEditor;
    private readonly TuiEffectHost _effects;
    private readonly ILogger<AvaloniaWorkspaceCommands> _logger;

    public AvaloniaWorkspaceCommands(
        ChatViewModel chat,
        SessionListViewModel sessions,
        CodeEditorViewModel codeEditor,
        TuiEffectHost effects,
        ILogger<AvaloniaWorkspaceCommands> logger)
    {
        _chat = chat;
        _sessions = sessions;
        _codeEditor = codeEditor;
        _effects = effects;
        _logger = logger;
    }

    public void NewSession() => _sessions.NewSessionCommand.Execute(null);

    public void BranchSession() => TaskFireAndForget.Forget(
        _sessions.BranchCommand.ExecuteAsync(null),
        ex => AvaloniaWorkspaceCommandsLog.BranchSessionFailed(_logger, ex));

    public void RefreshSessions() => TaskFireAndForget.Forget(
        _sessions.RefreshCommand.ExecuteAsync(null),
        ex => AvaloniaWorkspaceCommandsLog.RefreshSessionsFailed(_logger, ex));

    public async System.Threading.Tasks.Task OpenFileAsync() => await _codeEditor.OpenFileCommand.ExecuteAsync(null);
    public async System.Threading.Tasks.Task SaveFileAsync() => await _codeEditor.SaveCommand.ExecuteAsync(null);
    public void StopAgent() => _chat.StopCommand.Execute(null);
    public void ClearChat() => _chat.ClearCommand.Execute(null);
}

/// <summary>
///     SG12: BCL <c>[LoggerMessage]</c> delegates for <see cref="AvaloniaWorkspaceCommands" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AvaloniaWorkspaceCommandsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Branch session failed")]
    public static partial void BranchSessionFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Refresh sessions failed")]
    public static partial void RefreshSessionsFailed(ILogger logger, Exception ex);
}
