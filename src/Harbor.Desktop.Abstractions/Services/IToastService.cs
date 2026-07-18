using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.Services;

/// <summary>
///     Queue toast notifications and notify a platform renderer. The platform
///     toast container (Avalonia <c>ToastNotificationsView</c>, etc.) subscribes
///     to <see cref="ToastAdded"/> and projects each toast into the visual tree.
/// </summary>
public interface IToastService
{
    /// <summary>Raised when a new toast is queued. Payload is the new toast.</summary>
    event EventHandler<ToastNotification>? ToastAdded;

    /// <summary>Queue a toast with the given title, message, and kind. Uses the default duration.</summary>
    void Show(string title, string message, ToastKind kind);

    /// <summary>Queue an info toast.</summary>
    void ShowInfo(string title, string message)
        => Show(title, message, ToastKind.Info);

    /// <summary>Queue a success toast.</summary>
    void ShowSuccess(string title, string message)
        => Show(title, message, ToastKind.Success);

    /// <summary>Queue a warning toast.</summary>
    void ShowWarning(string title, string message)
        => Show(title, message, ToastKind.Warning);

    /// <summary>Queue an error toast.</summary>
    void ShowError(string title, string message)
        => Show(title, message, ToastKind.Error);
}
