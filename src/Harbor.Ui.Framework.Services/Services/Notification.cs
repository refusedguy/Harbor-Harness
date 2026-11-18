// Notification.cs — issue #2372: one notification value shared by the inline
// Alert and the toast queue. It carries only the external state (severity and
// message); the LOOK comes from the one presentation table
// (ToastPresentations), never from a second kind-to-glyph map.

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     The external state of a notification: a severity and a message.
/// </summary>
/// <remarks>
///     <para>
///         Harbor paints a notification in two places: the inline
///         <c>Alert</c> in the transcript, and the existing toast seat backed by
///         <c>ToastOverlay</c> and reached through <see cref="IToastService" />.
///         Both read THIS value. Neither decides what a severity looks like —
///         that is the one job of <see cref="ToastPresentations" />, the same
///         table the desktop converters read.
///     </para>
///     <para>
///         A value type on purpose: it is per-notification state handed to a
///         renderer, not a service and not an extension axis. Adding a new UI
///         surface that paints a notification does not require touching it.
///     </para>
/// </remarks>
/// <param name="Severity">The severity, one of the four <see cref="ToastKind" /> levels.</param>
/// <param name="Message">The human-readable message.</param>
public readonly record struct Notification(ToastKind Severity, string Message)
{
    /// <summary>
    ///     The presentation row for <see cref="Severity" />, read from the one
    ///     shared table.
    /// </summary>
    public ToastPresentation Presentation => ToastPresentations.For(Severity);

    /// <summary>The leading glyph from the shared presentation table.</summary>
    public char Glyph => Presentation.Glyph;

    /// <summary>Materialises the toast the existing queue enqueues.</summary>
    public ToastNotification ToToast() => new(Message, Severity);
}
