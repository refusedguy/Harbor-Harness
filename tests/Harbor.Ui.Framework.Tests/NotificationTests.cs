// NotificationTests.cs — issue #2372: the common notification primitive.
// It carries external severity + message and derives its presentation from the
// ONE shared table (ToastPresentations). No second kind-to-look map.

using Harbor.Ui.Framework.Services;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #2372 — the notification value both the inline Alert and the toast
///     queue present.
/// </summary>
public sealed class NotificationTests
{
    /// <summary>Every severity is presented by the shared table row for that kind.</summary>
    [Test]
    public async Task Every_Severity_Uses_The_Shared_Presentation_Row()
    {
        foreach ((ToastKind kind, ToastPresentation presentation) in ToastPresentations.All)
        {
            var notification = new Notification(kind, "message");

            await Assert.That(notification.Severity).IsEqualTo(kind);
            await Assert.That(notification.Message).IsEqualTo("message");
            await Assert.That(notification.Presentation).IsEqualTo(presentation)
                .Because($"{kind} must use the shared presentation row");
            await Assert.That(notification.Glyph).IsEqualTo(presentation.Glyph)
                .Because($"{kind} must lead with the shared glyph");
        }
    }

    /// <summary>An undeclared severity falls back to the table's informational row.</summary>
    [Test]
    public async Task Undeclared_Severity_Falls_Back_Like_The_Table_Does()
    {
        var notification = new Notification((ToastKind)999, "message");

        await Assert.That(notification.Presentation)
            .IsEqualTo(ToastPresentations.For(ToastKind.Info));
    }

    /// <summary>Materialising a toast keeps the severity and message intact.</summary>
    [Test]
    public async Task ToToast_Keeps_Severity_And_Message()
    {
        var notification = new Notification(ToastKind.Warning, "low disk");

        ToastNotification toast = notification.ToToast();

        await Assert.That(toast.Message).IsEqualTo("low disk");
        await Assert.That(toast.Kind).IsEqualTo(ToastKind.Warning);
        await Assert.That(toast.Id).IsNotEqualTo(Guid.Empty);
    }

    /// <summary>The value is a value: equal state compares equal.</summary>
    [Test]
    public async Task Equal_State_Compares_Equal()
    {
        var a = new Notification(ToastKind.Error, "boom");
        var b = new Notification(ToastKind.Error, "boom");

        await Assert.That(a).IsEqualTo(b);
    }
}
