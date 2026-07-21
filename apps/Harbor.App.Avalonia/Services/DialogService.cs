using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
using HorizontalAlignment = Avalonia.Layout.HorizontalAlignment;
using Orientation = Avalonia.Layout.Orientation;
using Thickness = Avalonia.Thickness;
using TextWrapping = Avalonia.Media.TextWrapping;

namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Modal dialog helpers — open file, save file, confirm, prompt.
///     Wraps Avalonia's <see cref="IStorageProvider" /> behind a synchronous-friendly API.
/// </summary>
public sealed class AvaloniaFilePicker : IFilePicker
{
    private readonly ILogger<AvaloniaFilePicker> _logger;

    /// <summary>Construct a <see cref="AvaloniaFilePicker" />.</summary>
    public AvaloniaFilePicker(ILogger<AvaloniaFilePicker> logger)
    {
        _logger = logger;
    }

    /// <summary>Get the main window (used as the parent for dialogs).</summary>
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple = false, CancellationToken cancellationToken = default)
    {
        var window = MainWindow;
        if (window is null) return Array.Empty<string>();
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple
        });
        return files.Select(f => f.Path.LocalPath).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> PickSaveFileAsync(string title, string defaultFileName, CancellationToken cancellationToken = default)
    {
        var window = MainWindow;
        if (window is null) return null;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName
        });
        return file?.Path.LocalPath;
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title = "Select Folder", CancellationToken cancellationToken = default)
    {
        var window = MainWindow;
        if (window is null) return null;
        var folder = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folder.Count > 0 ? folder[0].Path.LocalPath : null;
    }
}

/// <summary>
///     Modal dialog helpers — confirm / prompt / alert.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly ILogger<DialogService> _logger;

    /// <summary>Construct a <see cref="DialogService" />.</summary>
    public DialogService(ILogger<DialogService> logger)
    {
        _logger = logger;
    }

    /// <summary>Get the main window (used as the parent for dialogs).</summary>
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>Show a confirmation dialog with OK / Cancel buttons.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", string cancelLabel = "Cancel", CancellationToken cancellationToken = default)
    {
        var window = MainWindow;
        if (window is null) return false;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
        var ok = new Button { Content = okLabel, Classes = { "Primary" }, Padding = new Thickness(20, 6) };
        var cancel = new Button { Content = cancelLabel, Padding = new Thickness(20, 6) };
        var result = false;
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        dialog.Content = new StackPanel { Margin = new Thickness(0), Children = { text, buttons } };
        await dialog.ShowDialog(window);
        _logger.LogDebug("Confirm '{Title}' → {Result}", title, result);
        return result;
    }

    /// <summary>Show a prompt dialog.</summary>
    public async Task<string?> PromptAsync(string title, string message, string defaultValue = "", CancellationToken cancellationToken = default)
    {
        var window = MainWindow;
        if (window is null) return null;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var box = new TextBox { Text = defaultValue, PlaceholderText = message };
        var ok = new Button { Content = "OK", Classes = { "Primary" }, Padding = new Thickness(20, 6) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(20, 6) };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        string? result = await dialog.ShowDialog<string?>(window, cancellationToken);
        _logger.LogDebug("Prompt '{Title}' → '{Result}'", title, result);
        return result;
    }

    /// <summary>Show an informational alert with a single OK button.</summary>
    public async Task AlertAsync(string title, string message, string okLabel = "OK", CancellationToken cancellationToken = default)
    {
        var window = MainWindow;
        if (window is null) return;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
        var ok = new Button { Content = okLabel, Classes = { "Primary" }, Padding = new Thickness(20, 6), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Thickness(0), Children = { text, ok } };
        dialog.Content = panel;
        await dialog.ShowDialog(window);
        _logger.LogDebug("Alert '{Title}'", title);
    }
}

/// <summary>
///     Toast notification service. Other ViewModels push toasts via <see cref="Show" />
///     and the ToastNotificationsView renders them in the bottom-right corner.
/// </summary>
public sealed class ToastService : IToastService
{
    private readonly ILogger<ToastService> _logger;

    /// <summary>Construct a <see cref="ToastService" />.</summary>
    public ToastService(ILogger<ToastService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<ToastNotification>? ToastAdded;

    /// <summary>Queue an informational toast.</summary>
    /// <param name="message">Toast body.</param>
    public void Show(string message) => Show(message, ToastKind.Info);

    /// <summary>Queue a toast with the given kind.</summary>
    /// <param name="message">Toast body.</param>
    /// <param name="kind">Toast kind (Info, Success, Warning, Error).</param>
    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastNotification(Guid.NewGuid(), message, kind, DateTimeOffset.UtcNow);
        _logger.LogInformation("Toast [{Kind}]: {Message}", kind, message);
        ToastAdded?.Invoke(this, toast);
    }
}
