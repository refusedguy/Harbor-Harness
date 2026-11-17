// StarNagCardTests.cs — issue #1490: the star reminder rides the existing
// ToastOverlay seat, appears at most once per N launches, remembers "never",
// and is silent in plain/CI/headless/demo processes.

using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.StarNag;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

public sealed class StarNagCardTests
{
    private const int Cols = 48;
    private const int Rows = 14;

    private static readonly Rect Viewport = new(0, 0, Cols, Rows);

    /// <summary>Drives the golden the same way the REPL frame does: drain the pending queue, then paint.</summary>
    private static string CardArt(StarNagCard card, ToastOverlay toasts)
    {
        _ = card.TryShow(toasts, enabled: true);
        var buffer = TestRender.Buffer(Cols, Rows);
        Rect box = toasts.ComputeBounds(Viewport);
        toasts.Paint(buffer, box); // first pass drains pending
        toasts.Paint(buffer, box); // second pass paints
        return TestRender.Art(buffer);
    }

    [Test]
    public async Task AtMostOncePerInterval()
    {
        const int interval = 3;
        var toasts = new ToastOverlay();
        var card = new StarNagCard(interval);
        var shown = new List<bool>();

        for (int launch = 0; launch < 7; launch++)
        {
            shown.Add(card.TryShow(toasts, enabled: true));
        }

        // Launch 1 shows, launches 2-3 do not, launch 4 shows, and so on.
        await Assert.That(string.Join(",", shown.Select(s => s ? "1" : "0")))
            .IsEqualTo("1,0,0,1,0,0,1");
        await Assert.That(card.State.Launches).IsEqualTo(7);
        await Assert.That(card.State.LastShownAt).IsEqualTo(7);
        await Assert.That(toasts.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Dismiss_RemembersForever()
    {
        var toasts = new ToastOverlay();
        var card = new StarNagCard(2);

        _ = card.TryShow(toasts, enabled: true);
        card.Dismiss();

        for (int launch = 0; launch < 10; launch++)
        {
            await Assert.That(card.TryShow(toasts, enabled: true)).IsFalse();
        }

        await Assert.That(card.State.Dismissed).IsTrue();
        await Assert.That(toasts.Count).IsEqualTo(1); // only the pre-dismiss toast
    }

    [Test]
    public async Task StarLater_KeepsTheConfiguredCadence()
    {
        var toasts = new ToastOverlay();
        var card = new StarNagCard(4);

        _ = card.TryShow(toasts, enabled: true);
        card.StarLater();

        await Assert.That(card.TryShow(toasts, enabled: true)).IsFalse();
        await Assert.That(card.TryShow(toasts, enabled: true)).IsFalse();
        await Assert.That(card.TryShow(toasts, enabled: true)).IsFalse();
        await Assert.That(card.TryShow(toasts, enabled: true)).IsTrue();
    }

    [Test]
    public async Task Guard_PlainCiHeadlessDemo_AreAllSilent()
    {
        // The four silence cases and the one enabled case, all without touching
        // the process environment (parallel-safe).
        await Assert.That(StarNagCard.IsEnabled("plain", null, null, null)).IsFalse();
        await Assert.That(StarNagCard.IsEnabled(null, "true", null, null)).IsFalse();
        await Assert.That(StarNagCard.IsEnabled(null, null, "1", null)).IsFalse();
        await Assert.That(StarNagCard.IsEnabled(null, null, null, "1")).IsFalse();
        await Assert.That(StarNagCard.IsEnabled("cellforge", null, null, null)).IsTrue();

        var toasts = new ToastOverlay();
        var card = new StarNagCard(1);
        bool shown = card.TryShow(toasts, enabled: false);

        await Assert.That(shown).IsFalse();
        await Assert.That(toasts.Count).IsEqualTo(0);
        await Assert.That(card.State.Launches).IsEqualTo(0); // no work at all when disabled
    }

    [Test]
    public async Task Card_GoldenFrame()
    {
        var toasts = new ToastOverlay();
        var card = new StarNagCard(3);
        string art = CardArt(card, toasts);

        string doc = $"# golden: starnag-card\n## art ({Cols}x{Rows})\n{art}";
        string expected = CelldiffGoldens.Verify("starnag-card", doc);
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(art).Contains("Star Harbor on GitHub");
        await Assert.That(art).Contains("!"); // ToastKind.Warning glyph from the shared table
    }
}
