namespace Harbor.Ui.Framework.Navigation;

/// <summary>
///     Port for workspace-level commands that operate on chat, sessions,
///     editor, and agent. Palette and keyboard shortcuts depend on this port
///     instead of <c>MainViewModel</c>.
/// </summary>
public interface IWorkspaceCommands
{
    /// <summary>Create a new session.</summary>
    void NewSession();

    /// <summary>Branch the active session.</summary>
    void BranchSession();

    /// <summary>Refresh the session list.</summary>
    void RefreshSessions();

    /// <summary>Open a file in the editor.</summary>
    System.Threading.Tasks.Task OpenFileAsync();

    /// <summary>Save the current file.</summary>
    System.Threading.Tasks.Task SaveFileAsync();

    /// <summary>Stop the currently running agent.</summary>
    void StopAgent();

    /// <summary>Clear the chat transcript.</summary>
    void ClearChat();
}
