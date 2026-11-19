using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Views.Components;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

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
        (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

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
        (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

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
        var result = false;
        var content = new DialogContent();
        content.Bind(new DialogFormData(
            Message: message,
            OkLabel: okLabel,
            CancelLabel: cancelLabel,
            OkCommand: new RelayCommand(() => { result = true; dialog.Close(); }),
            CancelCommand: new RelayCommand(() => dialog.Close())));
        dialog.Content = content;
        await dialog.ShowDialog(window);
        DialogServiceLog.ConfirmResult(_logger, title, result);
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
        var content = new DialogContent();
        content.Bind(new DialogFormData(
            Message: message,
            OkLabel: "OK",
            CancelLabel: "Cancel",
            InputText: defaultValue,
            OkCommand: new RelayCommand(() => dialog.Close(content.InputText)),
            CancelCommand: new RelayCommand(() => dialog.Close(null))));
        dialog.Content = content;
        string? result = await dialog.ShowDialog<string?>(window);
        DialogServiceLog.PromptResult(_logger, title, result);
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
        var content = new DialogContent();
        content.Bind(new DialogFormData(
            Message: message,
            OkLabel: okLabel,
            OkCommand: new RelayCommand(() => dialog.Close())));
        dialog.Content = content;
        await dialog.ShowDialog(window);
        DialogServiceLog.AlertShown(_logger, title);
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
        var toast = new ToastNotification(message, kind);
        ToastServiceLog.ToastShown(_logger, kind, message);
        ToastAdded?.Invoke(this, toast);
    }
}

/// <summary>
///     SG12: BCL <c>[LoggerMessage]</c> delegates for <see cref="DialogService" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class DialogServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Confirm '{Title}' → {Result}")]
    public static partial void ConfirmResult(ILogger logger, string title, bool result);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Prompt '{Title}' → '{Result}'")]
    public static partial void PromptResult(ILogger logger, string title, string? result);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Alert '{Title}'")]
    public static partial void AlertShown(ILogger logger, string title);
}

/// <summary>
///     SG12: BCL <c>[LoggerMessage]</c> delegates for <see cref="ToastService" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ToastServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Toast [{Kind}]: {Message}")]
    public static partial void ToastShown(ILogger logger, ToastKind kind, string message);
}
