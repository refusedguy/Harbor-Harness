namespace Harbor.Ui.Framework.Services;
/// <summary>
///     Abstraction for toast notifications. Each desktop app implements this
///     to show toasts in its own UI framework.
/// </summary>
public interface IToastService
{
    /// <summary>Show a toast notification.</summary>
    public void Show(string message, ToastKind kind = ToastKind.Info);

    /// <summary>Raised when a toast is added (for VMs that need to track toasts).</summary>
    public event EventHandler<ToastNotification>? ToastAdded;
}

/// <summary>Toast notification kind.</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>A toast notification with id + timestamp.</summary>
public sealed record ToastNotification(
    Guid Id,
    string Message,
    ToastKind Kind,
    DateTimeOffset CreatedAt)
{
    /// <summary>Convenience constructor that generates a new id + timestamp.</summary>
    public ToastNotification(string message, ToastKind kind)
        : this(Guid.NewGuid(), message, kind, DateTimeOffset.UtcNow)
    {
    }
}
