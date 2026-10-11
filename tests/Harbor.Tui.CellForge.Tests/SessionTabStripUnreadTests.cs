using System.Collections.Immutable;
using System.Text;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Render tests for the #1173 unread model: the two-colour static marker
///     (amber activity vs red error), the settle fade after the signal
///     clears, and the new dispatch helpers. Geometry stays in
///     <see cref="SessionTabStripPanelTests" /> — this is paint behaviour.
/// </summary>
[NotInParallel("pty")]
public class SessionTabStripUnreadTests
{
    private static SessionId Sid(string id) => SessionId.Create(id);

    private static SessionTab Tab(string id, string title = "session") =>
        new(Sid(id), title);

    private static TabStripState Strip(int activeIndex, params SessionTab[] tabs) =>
        new()
        {
            Tabs = tabs.ToImmutableArray(),
            ActiveTabId = activeIndex >= 0 ? tabs[activeIndex].SessionId : null
        };

    private static SessionTabStripPanel StripPanel(TabStripState strip)
    {
        var panel = new SessionTabStripPanel();
        panel.Strip = strip;
        return panel;
    }

    private static ScreenBuffer Painted(SessionTabStripPanel panel, int width, int height)
    {
        var buffer = new ScreenBuffer(width, height);
        var tree = new LayoutTree();
        tree.AddRoot(panel);
        tree.Solve(width, height);
        panel.Paint(buffer);
        return buffer;
    }

    private static string Row(ScreenBuffer buffer, int y, int width)
    {
        var sb = new StringBuilder(width);
        for (int x = 0; x < width; x++)
        {
            var cell = buffer.Get(x, y);
            sb.Append(cell.Rune == 0 ? ' ' : (char)cell.Rune);
        }

        return sb.ToString();
    }

    private static int MarkerX(SessionTabStripPanel panel)
    {
        // Marker cell of an unread tab, right-pad cell of a clean one (where
        // a settling marker draws). Read fresh off every plan: clearing the
        // signal narrows the tab, so the column can move between paints.
        var cell = panel.LastPlan.Cells[0];
        return cell.TitleX + cell.TitleCells;
    }

    // ── two colours ──────────────────────────────────────────────────────

    [Test]
    public async Task ErrorTab_MarksRed_ActivityTab_MarksAmber()
    {
        var strip = Strip(0,
            new SessionTab(Sid("e"), "failing", HasError: true),
            new SessionTab(Sid("a"), "active", IsDirty: true),
            Tab("c", "clean"));
        var panel = StripPanel(strip);
        var buffer = Painted(panel, 60, SessionTabStripPanel.PreferredRows);

        await Assert.That(panel.LastPlan.Cells[0].HasDirtyMarker).IsTrue();
        await Assert.That(panel.LastPlan.Cells[0].HasErrorMarker).IsTrue();
        await Assert.That(panel.LastPlan.Cells[1].HasDirtyMarker).IsTrue();
        await Assert.That(panel.LastPlan.Cells[1].HasErrorMarker).IsFalse();
        await Assert.That(panel.LastPlan.Cells[2].HasDirtyMarker).IsFalse();

        var errorCell = panel.LastPlan.Cells[0];
        var activityCell = panel.LastPlan.Cells[1];
        var errorStyle = buffer.Get(
            errorCell.TitleX + errorCell.TitleCells, 0).Style;
        var activityStyle = buffer.Get(
            activityCell.TitleX + activityCell.TitleCells, 0).Style;

        await Assert.That(errorStyle).IsEqualTo(new CellStyle(ChatPalette.Error));
        await Assert.That(activityStyle).IsEqualTo(new CellStyle(ChatPalette.Warning));
        await Assert.That(errorStyle).IsNotEqualTo(activityStyle);
    }

    // ── settle fade ──────────────────────────────────────────────────────

    [Test]
    public async Task ClearedMarker_Lingers_ThenLeaves()
    {
        var panel = StripPanel(Strip(0,
            new SessionTab(Sid("a"), "alpha", IsDirty: true),
            Tab("b", "beta")));
        var first = Painted(panel, 60, SessionTabStripPanel.PreferredRows);
        await Assert.That(Row(first, 0, 60)[MarkerX(panel)]).IsEqualTo('•');

        // The signal clears (the tab was read); the marker settles instead of
        // blinking out — still on screen on the next frame …
        panel.Strip = Strip(0, Tab("a", "alpha"), Tab("b", "beta"));
        var settling = Painted(panel, 60, SessionTabStripPanel.PreferredRows);
        await Assert.That(Row(settling, 0, 60)[MarkerX(panel)]).IsEqualTo('•');

        // … and gone once the fade drains.
        ScreenBuffer last = settling;
        for (int i = 0; i < SessionTabStripPanel.MarkerFadeTicks + 1; i++)
            last = Painted(panel, 60, SessionTabStripPanel.PreferredRows);
        await Assert.That(Row(last, 0, 60)[MarkerX(panel)]).IsNotEqualTo('•');
    }

    [Test]
    public async Task RemarkedTab_CancelsTheSettle()
    {
        var panel = StripPanel(Strip(0,
            new SessionTab(Sid("a"), "alpha", IsDirty: true),
            Tab("b", "beta")));
        _ = Painted(panel, 60, SessionTabStripPanel.PreferredRows);

        panel.Strip = Strip(0, Tab("a", "alpha"), Tab("b", "beta"));
        _ = Painted(panel, 60, SessionTabStripPanel.PreferredRows);

        // Unread again before the drain finished: the full marker is back and
        // stays — no dim frame, no disappearance.
        panel.Strip = Strip(0,
            new SessionTab(Sid("a"), "alpha", IsDirty: true),
            Tab("b", "beta"));
        for (int i = 0; i < SessionTabStripPanel.MarkerFadeTicks + 2; i++)
            _ = Painted(panel, 60, SessionTabStripPanel.PreferredRows);

        var cell = panel.LastPlan.Cells[0];
        await Assert.That(cell.HasDirtyMarker).IsTrue();
        var buffer = Painted(panel, 60, SessionTabStripPanel.PreferredRows);
        await Assert.That(Row(buffer, 0, 60)[cell.TitleX + cell.TitleCells]).IsEqualTo('•');
    }

    // ── dispatch helpers ─────────────────────────────────────────────────

    [Test]
    public async Task DispatchHelpers_SendTheModelMessages()
    {
        var panel = StripPanel(Strip(0, Tab("a", "alpha"), Tab("b", "beta"), Tab("c", "gamma")));
        var received = new List<AppMsg>();
        panel.Dispatch = received.Add;

        await Assert.That(panel.Reopen()).IsTrue();
        await Assert.That(panel.NextUnread()).IsTrue();
        await Assert.That(panel.PreviousUnread()).IsTrue();
        await Assert.That(panel.ActivateSlot(2)).IsTrue();
        await Assert.That(panel.ActivateSlot(0)).IsFalse();
        await Assert.That(panel.ActivateSlot(10)).IsFalse();
        await Assert.That(panel.ActivateSlot(99)).IsFalse();

        await Assert.That(received.Count).IsEqualTo(4);
        await Assert.That(received[0]).IsTypeOf<ChatAppMsg.ReopenTab>();
        await Assert.That(received[1]).IsTypeOf<ChatAppMsg.CycleNextUnreadTab>();
        await Assert.That(received[2]).IsTypeOf<ChatAppMsg.CyclePreviousUnreadTab>();
        var slot = (ChatAppMsg.ActivateTabSlot)received[3];
        await Assert.That(slot.Slot).IsEqualTo(2);
    }

    [Test]
    public async Task DispatchHelpers_WithoutSink_AreReadOnly()
    {
        var panel = StripPanel(Strip(0, Tab("a", "alpha"), Tab("b", "beta")));

        await Assert.That(panel.Reopen()).IsFalse();
        await Assert.That(panel.NextUnread()).IsFalse();
        await Assert.That(panel.ActivateSlot(1)).IsFalse();
    }
}
