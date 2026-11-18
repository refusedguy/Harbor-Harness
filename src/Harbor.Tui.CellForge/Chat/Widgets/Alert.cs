// Alert.cs — issue #2372: the inline Alert primitive. Kind and message arrive
// from outside (the Notification value); the glyph comes from the one shared
// presentation table (ToastPresentations), so an Alert and a toast of the same
// severity look identical. Escape dismisses it. A notification is shown as a
// transient toast through the EXISTING IToastService seat, never a new layer
// and never a new extension axis (#555); StarNagCard's toast path is untouched.

using System.Text;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     Inline alert with four severities, driven by external state.
/// </summary>
/// <remarks>
///     <para>
///         The widget owns no presentation policy. <see cref="Glyph" /> is read
///         from <see cref="ToastPresentations" />, the one table the desktop
///         converters read too, and the accent colour is the same palette
///         decision <see cref="ToastOverlay" /> makes for its strip. An alert
///         and a toast of the same severity therefore share their looks by
///         construction rather than by two copies staying in sync.
///     </para>
///     <para>
///         Transient display is not a paint of this widget:
///         <see cref="ShowAsToast" /> hands the notification to the existing
///         <see cref="IToastService" /> abstraction, whose CellForge
///         implementation (<see cref="ToastOverlayToastService" />) enqueues it
///         on the already-shipped <see cref="ToastOverlay" /> queue.
///     </para>
/// </remarks>
public sealed class Alert
{
    /// <summary>Columns the accent strip takes — the same as the toast row.</summary>
    private const int AccentWidth = 3;

    /// <summary>A strip narrower than this cannot hold an accent and a glyph.</summary>
    private const int MinWidth = AccentWidth + 2;

    public Alert(Notification notification)
    {
        Notification = notification;
    }

    public Alert(ToastKind severity, string message)
        : this(new Notification(severity, message))
    {
    }

    /// <summary>The external state this alert presents.</summary>
    public Notification Notification { get; }

    /// <summary>The severity, one of the four shared <see cref="ToastKind" /> levels.</summary>
    public ToastKind Severity => Notification.Severity;

    /// <summary>The message to show.</summary>
    public string Message => Notification.Message;

    /// <summary>The glyph the shared presentation table names for this severity.</summary>
    public char Glyph => Notification.Glyph;

    /// <summary>Whether a dismissal key was pressed.</summary>
    public bool IsDismissed { get; private set; }

    /// <summary>
    ///     Escape closes an inline alert. Any other key is left for the host.
    /// </summary>
    /// <returns>True when this key dismissed the alert.</returns>
    public bool TryDismiss(UiKeyCode key)
    {
        if (IsDismissed || key != UiKeyCode.Escape)
        {
            return false;
        }
        IsDismissed = true;
        return true;
    }

    /// <summary>
    ///     Shows this notification as a transient toast through the existing
    ///     toast abstraction. No new layer, queue or extension axis.
    /// </summary>
    public void ShowAsToast(IToastService toasts) => toasts.Show(Message, Severity);

    /// <summary>
    ///     Paints one alert row into <paramref name="buffer" /> at
    ///     <paramref name="rect" />: an accent strip, the shared glyph, a space
    ///     and the message, truncated with an ellipsis when it does not fit.
    ///     A dismissed or empty alert paints nothing.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        if (IsDismissed || string.IsNullOrEmpty(Message))
        {
            return;
        }
        if (rect.Width < MinWidth || rect.Height < 1)
        {
            return;
        }
        if (rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        // The accent is a PALETTE COLOUR, not a presentation key, so it does not
        // come from the shared table — exactly as in ToastOverlay.PaintToast.
        // The kind still decides the colour; the decision just lives here next
        // to the palette instead of in Harbor.Ui.Framework.Services.
        var accentColor = Severity switch
        {
            ToastKind.Success => ChatPalette.Success,
            ToastKind.Warning => ChatPalette.Warning,
            ToastKind.Error => ChatPalette.Error,
            _ => ChatPalette.Accent,
        };
        var accentStyle = new CellStyle(accentColor);
        var fillStyle = new CellStyle(ChatPalette.Panel);
        var textStyle = new CellStyle(ChatPalette.Accent);

        int right = Math.Min(rect.X + rect.Width - 1, buffer.Cols - 1);
        for (int x = rect.X; x <= right; x++)
        {
            buffer.At(x, rect.Y) = Cell.From(new Rune(' '), fillStyle);
        }
        for (int dy = 0; dy < AccentWidth && rect.Y + dy < buffer.Rows; dy++)
        {
            buffer.At(rect.X, rect.Y + dy) = Cell.From(new Rune('█'), accentStyle);
        }

        int textX = rect.X + AccentWidth;
        int textW = right - textX + 1;
        if (textW <= 0)
        {
            return;
        }

        // The row is "<glyph> <message>", truncated to textW - 1 characters
        // plus an ellipsis — the same characters, in the same order, as the
        // toast row. The span is written once per paint and never escapes.
        int full = 2 + Message.Length;
        bool ellipsis = full > textW;
        int take = ellipsis ? Math.Max(0, textW - 1) : full;
        Span<char> row = stackalloc char[textW + 1];

        int len = 0;
        if (take > 0)
        {
            row[len++] = Glyph;
        }
        if (take > 1)
        {
            row[len++] = ' ';
        }
        int message = Math.Min(Math.Max(0, take - 2), Message.Length);
        if (message > 0)
        {
            Message.AsSpan(0, message).CopyTo(row[len..]);
            len += message;
        }
        if (ellipsis)
        {
            row[len++] = '…';
        }

        buffer.SetText(textX, rect.Y, row[..len], textStyle);
    }
}
