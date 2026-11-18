// AlertTests.cs — issue #2372: the inline Alert shares the one presentation
// table with the toast seat, dismisses on Escape, and routes notifications
// through the existing IToastService instead of a new layer.

using Harbor.TestKit;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.Tests;

public sealed class AlertTests
{
    private const int Cols = 20;
    private const int Rows = 3;

    private static readonly Rect Viewport = new(0, 0, Cols, Rows);

    private static string AlertArt(Alert alert)
    {
        var buffer = TestRender.Buffer(Cols, Rows);
        alert.Paint(buffer, Viewport);
        return TestRender.Art(buffer);
    }

    /// <summary>
    ///     The inline row is byte-identical to the shape the toast strip paints
    ///     for the same severity: a one-column, three-row accent bar, two fill
    ///     cells, then the shared glyph, a space and the message.
    /// </summary>
    [Test]
    public async Task Inline_Alert_GoldenFrame()
    {
        var alert = new Alert(ToastKind.Error, "disk full");
        string art = AlertArt(alert);

        string doc = $"# golden: alert-inline\n## art ({Cols}x{Rows})\n{art}";
        string expected = CelldiffGoldens.Verify("alert-inline", doc);
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(art).Contains("✗");
    }

    /// <summary>
    ///     Every severity leads with the glyph the SHARED table names — the
    ///     same row the ToastOverlayGlyphTests pin for the toast seat. No
    ///     second kind-to-glyph map may creep into the Alert.
    /// </summary>
    [Test]
    public async Task Every_Severity_Leads_With_The_Shared_Tables_Glyph()
    {
        foreach ((ToastKind kind, ToastPresentation presentation) in ToastPresentations.All)
        {
            var alert = new Alert(kind, "hello");
            await Assert.That(alert.Glyph).IsEqualTo(presentation.Glyph)
                .Because($"{kind} must lead with the glyph ToastPresentations names");
            await Assert.That(alert.Presentation).IsEqualTo(presentation)
                .Because($"{kind} must present with the shared presentation row");
        }
    }

    /// <summary>Escape dismisses the alert; every other key leaves it up.</summary>
    [Test]
    public async Task Escape_Dismisses_Other_Keys_Do_Not()
    {
        var alert = new Alert(ToastKind.Warning, "careful");

        await Assert.That(alert.TryDismiss(UiKeyCode.Enter)).IsFalse();
        await Assert.That(alert.IsDismissed).IsFalse();

        await Assert.That(alert.TryDismiss(UiKeyCode.Escape)).IsTrue();
        await Assert.That(alert.IsDismissed).IsTrue();

        // Dismissal is idempotent: the alarm stays down.
        await Assert.That(alert.TryDismiss(UiKeyCode.Escape)).IsFalse();
    }

    /// <summary>A dismissed alert paints nothing at all.</summary>
    [Test]
    public async Task Dismissed_Alert_Paints_Nothing()
    {
        var alert = new Alert(ToastKind.Error, "disk full");
        _ = alert.TryDismiss(UiKeyCode.Escape);

        var buffer = TestRender.Buffer(Cols, Rows);
        alert.Paint(buffer, Viewport);

        await Assert.That(TestRender.Art(buffer)).IsEqualTo(TestRender.Art(TestRender.Buffer(Cols, Rows)));
    }

    /// <summary>
    ///     A notification shown as a transient toast goes through the existing
    ///     IToastService abstraction and lands on the already-shipped
    ///     ToastOverlay queue — no new layer.
    /// </summary>
    [Test]
    public async Task ShowAsToast_Routes_Through_The_Existing_Seat()
    {
        var toasts = new ToastOverlay();
        var service = new ToastOverlayToastService(toasts);
        var alert = new Alert(new Notification(ToastKind.Success, "saved"));
        ToastNotification? reported = null;
        service.ToastAdded += (_, toast) => reported = toast;

        alert.ShowAsToast(service);

        // The event is raised with the SAME instance that enters the queue.
        await Assert.That(reported).IsNotNull();
        await Assert.That(reported!.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(toasts.Count).IsEqualTo(1);

        // ToastOverlay needs a viewport wide enough for its 28-column strip.
        var buffer = TestRender.Buffer(48, 6);
        Rect box = toasts.ComputeBounds(new Rect(0, 0, 48, 6));
        toasts.Paint(buffer, box); // first pass drains the queue
        await Assert.That(toasts.Active.Count).IsEqualTo(1);
        await Assert.That(toasts.Active[0].Id).IsEqualTo(reported.Id);
        await Assert.That(toasts.Active[0].Message).IsEqualTo("saved");
        await Assert.That(toasts.Active[0].Kind).IsEqualTo(ToastKind.Success);
    }
}
