// ToastOverlayToastService.cs — issue #2372: the CellForge implementation of
// the EXISTING IToastService abstraction. It does not add a queue, a layer or an
// extension axis: it forwards to the already-shipped ToastOverlay seat that
// StarNagCard and the rest of the TUI already use.

using Harbor.Ui.Framework.Services;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     Bridges <see cref="IToastService" /> onto the existing
///     <see cref="ToastOverlay" /> queue for the CellForge renderer.
/// </summary>
/// <remarks>
///     The desktop shell has its own implementation. This one exists so an
///     inline <see cref="Alert"/> can hand a notification to the same
///     abstraction the other surfaces use, without Harbor inventing a second
///     notification service (#2372).
/// </remarks>
public sealed class ToastOverlayToastService : IToastService
{
    private readonly ToastOverlay _overlay;

    public ToastOverlayToastService(ToastOverlay overlay)
    {
        _overlay = overlay;
    }

    /// <inheritdoc />
    public event EventHandler<ToastNotification>? ToastAdded;

    /// <inheritdoc />
    public void Show(string message, ToastKind kind = ToastKind.Info) =>
        Show(new ToastNotification(message, kind));

    /// <summary>Shows a notification materialised from the shared primitive.</summary>
    public void Show(Notification notification) => Show(notification.ToToast());

    private void Show(ToastNotification toast)
    {
        _overlay.Show(toast);
        ToastAdded?.Invoke(this, toast);
    }
}
