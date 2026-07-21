namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Base for the toast-notification view-model. Holds the visible toast
///     collection. The platform <c>IToastService</c> raises
///     <see cref="IToastService.ToastAdded" />; the derived VM subscribes and
///     appends a wrapped toast here.
/// </summary>
public abstract class ToastNotificationViewModelBase : ViewModelBase
{
    /// <summary>Construct a <see cref="ToastNotificationViewModelBase" />.</summary>
    protected ToastNotificationViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Visible toasts.</summary>
    public ObservableCollection<ActiveToast> ActiveToasts { get; } = new();

    /// <summary>Add a toast to <see cref="ActiveToasts" />.</summary>
    protected void AddToast(ToastNotification toast)
    {
        var active = new ActiveToast(toast, () => RemoveToast(toast));
        ActiveToasts.Add(active);
    }

    /// <summary>Remove a toast from <see cref="ActiveToasts" /> (auto-dismiss or user-clicked).</summary>
    protected void RemoveToast(ToastNotification toast)
    {
        for (int i = 0; i < ActiveToasts.Count; i++)
        {
            if (ActiveToasts[i].Notification == toast)
            {
                ActiveToasts.RemoveAt(i);
                return;
            }
        }
    }
}

/// <summary>
///     One visible toast. Wraps the immutable <see cref="ToastNotification" />
///     with a dismiss action the view can invoke.
/// </summary>
/// <param name="Notification">The underlying toast payload.</param>
/// <param name="Dismiss">Callback to dismiss this toast (auto or user-click).</param>
public sealed record ActiveToast(
    ToastNotification Notification,
    Action Dismiss);
