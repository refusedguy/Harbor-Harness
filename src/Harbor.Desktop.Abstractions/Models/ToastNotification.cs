namespace Harbor.Desktop.Abstractions.Models;

/// <summary>
///     Immutable description of a toast notification. Platform toast containers
///     (Avalonia <c>ToastNotificationsView</c>, WPF <c>ToastNotificationsView</c>,
///     Blazor <c>ToastContainer.razor</c>) project this into their own visual tree.
/// </summary>
/// <param name="Title">Short headline shown in bold.</param>
/// <param name="Message">Body text.</param>
/// <param name="Kind">Severity — drives the accent color.</param>
/// <param name="Duration">How long to display before auto-dismiss. <see cref="TimeSpan.Zero"/> means sticky.</param>
public sealed record ToastNotification(
    string Title,
    string Message,
    ToastKind Kind,
    TimeSpan Duration);

/// <summary>
///     Factory-style helpers for the most common toasts. Avoids each callsite
///     having to repeat the same <see cref="TimeSpan"/> literals.
/// </summary>
public static class ToastNotificationExtensions
{
    /// <summary>Default auto-dismiss for Info/Success toasts.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);

    /// <summary>Default auto-dismiss for Warning/Error toasts (slightly longer).</summary>
    public static readonly TimeSpan LongDuration = TimeSpan.FromSeconds(6);

    /// <summary>Build an info toast with the default duration.</summary>
    public static ToastNotification Info(string title, string message)
        => new(title, message, ToastKind.Info, DefaultDuration);

    /// <summary>Build a success toast with the default duration.</summary>
    public static ToastNotification Success(string title, string message)
        => new(title, message, ToastKind.Success, DefaultDuration);

    /// <summary>Build a warning toast with the long duration.</summary>
    public static ToastNotification Warning(string title, string message)
        => new(title, message, ToastKind.Warning, LongDuration);

    /// <summary>Build an error toast with the long duration.</summary>
    public static ToastNotification Error(string title, string message)
        => new(title, message, ToastKind.Error, LongDuration);
}
