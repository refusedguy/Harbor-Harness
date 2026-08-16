namespace Harbor.Ui.Framework.Navigation;

/// <summary>
///     Port for shell-level chrome actions. Desktop and future renderers
///     implement this; consumers (palette, hotkeys, views) depend only on the port.
/// </summary>
public interface IShellChrome
{
    /// <summary>Navigate the active content to the given route.</summary>
    void Navigate(string route);

    /// <summary>Toggle the sidebar / right drawer visibility.</summary>
    void ToggleSidebar();

    /// <summary>Open an overlay by id (e.g. "palette", "settings", "diff").</summary>
    void OpenOverlay(string id);

    /// <summary>Close an overlay by id.</summary>
    void CloseOverlay(string id);

    /// <summary>
    ///     Close the topmost overlay, if any. Returns <c>true</c> if an overlay was closed.
    /// </summary>
    bool CloseTopOverlay();

    /// <summary>Toggle between dark and light theme.</summary>
    void ToggleTheme();
}
