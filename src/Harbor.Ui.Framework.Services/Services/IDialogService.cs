namespace Harbor.Ui.Framework.Services;
/// <summary>
///     Modal dialog primitives — confirm, prompt, alert. Platform apps
///     implement this with their own native dialog primitives (Avalonia
///     <c>Window.ShowDialog</c>, WPF <c>MessageBox</c>, MAUI
///     <c>DisplayActionSheet</c>, Blazor custom modal component).
/// </summary>
public interface IDialogService
{
    /// <summary>Show a confirmation dialog with OK / Cancel buttons.</summary>
    /// <returns>True if the user clicked OK; false otherwise.</returns>
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string okLabel = "OK",
        string cancelLabel = "Cancel",
        CancellationToken cancellationToken = default);

    /// <summary>Show a single-line prompt and return the user's text.</summary>
    /// <returns>The entered text, or null if the user cancelled.</returns>
    public Task<string?> PromptAsync(
        string title,
        string message,
        string defaultValue = "",
        CancellationToken cancellationToken = default);

    /// <summary>Show an informational alert with a single OK button.</summary>
    public Task AlertAsync(
        string title,
        string message,
        string okLabel = "OK",
        CancellationToken cancellationToken = default);
}
