namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Cross-platform file-picker contract. Each platform implements this with
///     its own native picker (Avalonia <c>IStorageProvider</c>, WPF
///     <c>Microsoft.Win32.OpenFileDialog</c>, MAUI <c>FilePicker</c>,
///     Blazor → download/upload interop).
/// </summary>
public interface IFilePicker
{
    /// <summary>Open an "Open file" picker and return the chosen path(s).</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="allowMultiple">Allow multi-select.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected paths, or an empty list if the user cancelled.</returns>
    Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        bool allowMultiple = false,
        CancellationToken cancellationToken = default);

    /// <summary>Open a "Save file" picker and return the chosen path.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="defaultFileName">Default file name to pre-populate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected path, or null if the user cancelled.</returns>
    Task<string?> PickSaveFileAsync(
        string title,
        string defaultFileName,
        CancellationToken cancellationToken = default);

    /// <summary>Open a "Pick folder" picker and return the chosen path.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected folder path, or null if the user cancelled.</returns>
    Task<string?> PickFolderAsync(
        string title = "Select Folder",
        CancellationToken cancellationToken = default);
}
