using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the toast-notification view-model. Holds the visible toast
///     collection. Projected from the store via <see cref="StoreSubscriberViewModel" />
///     selectors; the platform <c>IToastService</c> raises
///     <see cref="IToastService.ToastAdded" />; the derived VM subscribes and
///     appends a wrapped toast here.
/// </summary>
public abstract class ToastNotificationViewModelBase : StoreSubscriberViewModel
{
    /// <summary>Construct a <see cref="ToastNotificationViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller and store binder.</param>
    /// <param name="logger">Logger.</param>
    protected ToastNotificationViewModelBase(
        IDispatcherAdapter dispatcher,
        ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => GetActiveToasts(), v => SyncToasts(v));
    }

    /// <summary>Visible toasts.</summary>
    public ObservableCollection<ActiveToast> ActiveToasts { get; } = new();

    /// <summary>
    ///     Override in a derived view-model to project toast state from the store
    ///     (e.g. <see cref="ChromeViewState.Toasts" />). The default returns an
    ///     empty array so the base class is safe to use without store integration.
    /// </summary>
    /// <returns>Active toast notifications to display.</returns>
    protected virtual ImmutableArray<ToastNotification> GetActiveToasts()
        => ImmutableArray<ToastNotification>.Empty;

    /// <summary>
    ///     Called when the global <see cref="UiState" /> changes. Applies all
    ///     declared selectors to project state slices into view-model properties.
    /// </summary>
    /// <param name="state">The current UI state snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }

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

    private void SyncToasts(ImmutableArray<ToastNotification> toasts)
    {
        ActiveToasts.Clear();
        foreach (var toast in toasts)
        {
            ActiveToasts.Add(new ActiveToast(toast, () => RemoveToast(toast)));
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
