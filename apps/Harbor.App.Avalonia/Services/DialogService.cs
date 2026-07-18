using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment;
using Orientation = global::Avalonia.Layout.Orientation;
using Thickness = global::Avalonia.Thickness;
using TextWrapping = global::Avalonia.Media.TextWrapping;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Modal dialog helpers — open file, save file, confirm, prompt.
///     Wraps Avalonia's <see cref="IStorageProvider"/> behind a synchronous-friendly API.
/// </summary>
public sealed class AvaloniaFilePicker
{
    private readonly ILogger<AvaloniaFilePicker> _logger;

    /// <summary>Construct a <see cref="AvaloniaFilePicker"/>.</summary>
    public AvaloniaFilePicker(ILogger<AvaloniaFilePicker> logger)
    {
        _logger = logger;
    }

    /// <summary>Get the main window (used as the parent for dialogs).</summary>
    private static Window? MainWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    /// <summary>Open a file picker and return the chosen path (or null if cancelled).</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="allowMultiple">Allow multi-select.</param>
    /// <returns>The selected file path(s), or null if the user cancelled.</returns>
    public async Task<IReadOnlyList<string>?> PickFilesAsync(string title, bool allowMultiple = false)
    {
        var window = MainWindow;
        if (window is null) return null;
        var provider = window.StorageProvider;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
                new FilePickerFileType("Code") { Patterns = new[] { "*.cs", "*.ts", "*.js", "*.json", "*.md", "*.py", "*.go", "*.rs" } },
            }
        }).ConfigureAwait(false);
        if (files is null || files.Count == 0) return null;
        var paths = files.Select(f => f.Path.LocalPath).ToList();
        _logger.LogDebug("Picked files: {Files}", string.Join(", ", paths));
        return paths;
    }

    /// <summary>Open a save-file picker and return the chosen path (or null if cancelled).</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="defaultFileName">Default file name to pre-populate.</param>
    /// <returns>The selected file path, or null if the user cancelled.</returns>
    public async Task<string?> PickSaveFileAsync(string title, string defaultFileName)
    {
        var window = MainWindow;
        if (window is null) return null;
        var provider = window.StorageProvider;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            DefaultExtension = "txt",
        }).ConfigureAwait(false);
        return file?.Path.LocalPath;
    }

    /// <summary>Pick a folder.</summary>
    public async Task<string?> PickFolderAsync(string title = "Select Folder")
    {
        var window = MainWindow;
        if (window is null) return null;
        var provider = window.StorageProvider;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(false);
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}

/// <summary>
///     Modal dialog helpers — confirm / prompt / alert.
/// </summary>
public sealed class DialogService
{
    private readonly ILogger<DialogService> _logger;

    /// <summary>Construct a <see cref="DialogService"/>.</summary>
    public DialogService(ILogger<DialogService> logger)
    {
        _logger = logger;
    }

    /// <summary>Show a confirmation dialog.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog body text.</param>
    /// <param name="okLabel">OK button label.</param>
    /// <param name="cancelLabel">Cancel button label.</param>
    /// <returns>True if the user clicked OK; false otherwise.</returns>
    public async Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", string cancelLabel = "Cancel")
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null) return false;

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        var result = false;
        var ok = new Button { Content = okLabel, Classes = { "Primary" }, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 6) };
        var cancel = new Button { Content = cancelLabel, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 6) };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => { result = false; dialog.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = global::Avalonia.Media.Brushes.White });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        // ShowDialog must run on the UI thread (it pumps the Avalonia message loop
        // and parents the dialog to the main window). The previous ConfigureAwait(false)
        // was technically safe — the continuation only reads a local bool and calls
        // the logger — but it is fragile: any future UI access added after the await
        // would silently break under a non-UI continuation. Stay on the UI thread.
        await dialog.ShowDialog(window);
        _logger.LogDebug("Confirm '{Title}' → {Result}", title, result);
        return result;
    }

    /// <summary>Show a prompt dialog.</summary>
    public async Task<string?> PromptAsync(string title, string message, string defaultValue = "")
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null) return null;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        var box = new TextBox { Text = defaultValue, Watermark = message };
        var ok = new Button { Content = "OK", Classes = { "Primary" }, Padding = new Thickness(20, 6) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(20, 6) };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        // ShowDialog must run on the UI thread (see ConfirmAsync comment). Stay on
        // the UI thread — the continuation only reads the local result string and
        // logs, but we keep the await UI-bound to stay safe under future edits.
        var result = await dialog.ShowDialog<string?>(window);
        _logger.LogDebug("Prompt '{Title}' → '{Result}'", title, result);
        return result;
    }
}

/// <summary>
///     Toast notification service. Other ViewModels push toasts via <see cref="Show"/>
///     and the ToastNotificationsView renders them in the bottom-right corner.
/// </summary>
public sealed class ToastService
{
    private readonly ILogger<ToastService> _logger;

    /// <summary>Raised when a new toast arrives. The toast container view subscribes.</summary>
    public event EventHandler<ToastNotification>? ToastAdded;

    /// <summary>Construct a <see cref="ToastService"/>.</summary>
    public ToastService(ILogger<ToastService> logger)
    {
        _logger = logger;
    }

    /// <summary>Queue an informational toast.</summary>
    /// <param name="message">Toast body.</param>
    public void Show(string message) => Show(message, ToastKind.Info);

    /// <summary>Queue a toast with the given kind.</summary>
    /// <param name="message">Toast body.</param>
    /// <param name="kind">Toast kind (Info, Success, Warning, Error).</param>
    public void Show(string message, ToastKind kind)
    {
        var toast = new ToastNotification(Guid.NewGuid(), message, kind, DateTimeOffset.UtcNow);
        _logger.LogInformation("Toast [{Kind}]: {Message}", kind, message);
        ToastAdded?.Invoke(this, toast);
    }
}

/// <summary>Kind of toast.</summary>
public enum ToastKind
{
    /// <summary>Informational toast (blue).</summary>
    Info,

    /// <summary>Success toast (green).</summary>
    Success,

    /// <summary>Warning toast (peach).</summary>
    Warning,

    /// <summary>Error toast (red).</summary>
    Error,
}

/// <summary>One toast notification. Immutable.</summary>
public sealed record ToastNotification(Guid Id, string Message, ToastKind Kind, DateTimeOffset CreatedAt);
