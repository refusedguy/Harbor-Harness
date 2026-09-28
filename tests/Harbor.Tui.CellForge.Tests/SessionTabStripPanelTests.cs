using System.Collections.Immutable;
using System.Text;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Tab-strip render tests for #389 (slice 2/3). The panel reads
///     <see cref="TabStripState" /> and holds no selection of its own, so these
///     assert the <i>plan</i> (pure geometry) plus the painted buffer — never a
///     private field, which is the point of the design.
/// </summary>
[NotInParallel("pty")]
public class SessionTabStripPanelTests
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
        // Panel.Rect has an internal setter, so drive the panel through a real
        // layout solve — the same path production uses.
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

    // ── visibility ──────────────────────────────────────────────────────────

    [Test]
    public async Task SingleTab_IsHidden_NoDeadChrome()
    {
        var plan = StripPanel(Strip(0, Tab("a", "only"))).Layout(80);
        await Assert.That(plan.IsVisible).IsFalse();
        await Assert.That(plan.Cells.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ZeroTabs_IsHidden()
    {
        var plan = StripPanel(TabStripState.Empty).Layout(80);
        await Assert.That(plan.IsVisible).IsFalse();
    }

    [Test]
    public async Task ForceShow_RevealsSingleTab()
    {
        // Config escape hatch: a user who wants a one-tab bar gets one.
        var panel = StripPanel(Strip(0, Tab("a", "only")) with { ForceShow = true });
        var plan = panel.Layout(80);
        await Assert.That(plan.IsVisible).IsTrue();
        await Assert.That(plan.Cells.Length).IsEqualTo(1);
        await Assert.That(plan.Cells[0].Title).IsEqualTo("only");
    }

    [Test]
    public async Task TwoTabs_AreVisible()
    {
        var plan = StripPanel(Strip(1, Tab("a", "alpha"), Tab("b", "beta"))).Layout(80);
        await Assert.That(plan.IsVisible).IsTrue();
        await Assert.That(plan.Cells.Length).IsEqualTo(2);
        await Assert.That(plan.Cells[1].IsActive).IsTrue();
        await Assert.That(plan.Cells[0].IsActive).IsFalse();
    }

    // ── painting at several widths ──────────────────────────────────────────

    [Test]
    public async Task Paints_Titles_And_ActiveUnderline()
    {
        var panel = StripPanel(Strip(1, Tab("a", "alpha"), Tab("b", "beta")));
        var buffer = Painted(panel, 60, SessionTabStripPanel.PreferredRows);

        var titles = Row(buffer, 0, 60);
        await Assert.That(titles).Contains("alpha");
        await Assert.That(titles).Contains("beta");

        // Only the focused tab is underlined, and across its own span: the
        // inactive neighbour must stay blank or the marker reads as "both".
        var underline = Row(buffer, 1, 60);
        int firstTab = panel.LastPlan.Cells[0].X;
        int secondTab = panel.LastPlan.Cells[1].X;
        await Assert.That(underline[secondTab]).IsNotEqualTo(' ');
        await Assert.That(underline[secondTab + 1]).IsNotEqualTo(' ');
        await Assert.That(underline[firstTab]).IsEqualTo(' ');
        await Assert.That(underline[firstTab + 1]).IsEqualTo(' ');
    }

    [Test]
    public async Task Paints_DirtyAndPinnedMarkers()
    {
        var strip = new TabStripState
        {
            Tabs = ImmutableArray.Create(
                new SessionTab(Sid("a"), "alpha", IsDirty: true, IsPinned: true),
                new SessionTab(Sid("b"), "beta")),
            ActiveTabId = Sid("a")
        };
        var panel = StripPanel(strip);
        var buffer = Painted(panel, 60, SessionTabStripPanel.PreferredRows);
        var titles = Row(buffer, 0, 60);

        await Assert.That(panel.LastPlan.Cells[0].HasDirtyMarker).IsTrue();
        await Assert.That(panel.LastPlan.Cells[0].HasPinMarker).IsTrue();
        await Assert.That(panel.LastPlan.Cells[1].HasDirtyMarker).IsFalse();

        // Pin marker sits left of the status dot, dirty marker right of the title.
        var cell = panel.LastPlan.Cells[0];
        await Assert.That(titles[cell.X]).IsEqualTo('▪');
        await Assert.That(titles[cell.X + SessionTabStripPanel.PinWidth]).IsEqualTo('●');
        await Assert.That(titles[cell.TitleX + cell.TitleCells]).IsEqualTo('•');
    }

    [Test]
    public async Task Overflow_Width_ShowsPlusN()
    {
        var tabs = Enumerable.Range(0, 12)
            .Select(i => Tab($"s{i}", $"session-number-{i}"))
            .ToArray();
        var panel = StripPanel(Strip(0, tabs));
        var plan = panel.Layout(40);

        await Assert.That(plan.IsVisible).IsTrue();
        await Assert.That(plan.Cells.Length).IsLessThan(12);
        await Assert.That(plan.HiddenTotal).IsGreaterThan(0);
        await Assert.That(plan.OverflowX).IsGreaterThanOrEqualTo(0);

        var buffer = Painted(panel, 40, SessionTabStripPanel.PreferredRows);
        var titles = Row(buffer, 0, 40);
        await Assert.That(titles).Contains($"+{plan.HiddenRight}");
    }

    [Test]
    public async Task ActiveTab_StaysVisible_WhenStripOverflows()
    {
        var tabs = Enumerable.Range(0, 10)
            .Select(i => Tab($"s{i}", $"session-{i}"))
            .ToArray();
        // Focus the last tab in a strip far too narrow for all of them.
        var panel = StripPanel(Strip(9, tabs));
        var plan = panel.Layout(40);

        var activeIndex = -1;
        for (int i = 0; i < plan.Cells.Length; i++)
        {
            if (plan.Cells[i].IsActive)
                activeIndex = plan.Cells[i].Index;
        }

        await Assert.That(activeIndex).IsEqualTo(9);
        await Assert.That(plan.HiddenLeft).IsGreaterThan(0);
        await Assert.That(plan.MoreLeft).IsTrue();
    }

    [Test]
    public async Task Scroll_FollowsFocus_EachDirection()
    {
        var tabs = Enumerable.Range(0, 8)
            .Select(i => Tab($"s{i}", $"session-{i}"))
            .ToArray();

        // Focus moving right walks the window right; moving back walks it left.
        var forward = StripPanel(Strip(6, tabs)).Layout(40);
        var back = StripPanel(Strip(1, tabs)).Layout(40);

        await Assert.That(forward.FirstVisible).IsGreaterThan(0);
        await Assert.That(back.FirstVisible).IsEqualTo(0);
        await Assert.That(back.HiddenRight).IsGreaterThan(0);
    }

    [Test]
    public async Task MinimumWidth_NeverOverlapsNeighbour()
    {
        // The whole point of clamping: at absurd widths every cell still fits
        // inside the row, so nothing bleeds into the composer below.
        var tabs = Enumerable.Range(0, 6)
            .Select(i => Tab($"s{i}", $"a-rather-long-session-title-{i}"))
            .ToArray();
        var panel = StripPanel(Strip(2, tabs));

        for (int width = 1; width <= 12; width++)
        {
            var plan = panel.Layout(width);
            for (int i = 0; i < plan.Cells.Length; i++)
            {
                var cell = plan.Cells[i];
                await Assert.That(cell.X).IsGreaterThanOrEqualTo(0);
                await Assert.That(cell.Width).IsGreaterThanOrEqualTo(0);
                await Assert.That(cell.X + cell.Width).IsLessThanOrEqualTo(width);
            }

            if (plan.OverflowX >= 0)
                await Assert.That(plan.OverflowX).IsLessThanOrEqualTo(width);
        }
    }

    [Test]
    public async Task ZeroWidth_HidesCleanly()
    {
        var plan = StripPanel(Strip(1, Tab("a", "alpha"), Tab("b", "beta"))).Layout(0);
        await Assert.That(plan.IsVisible).IsFalse();
    }

    [Test]
    public async Task SingleRow_Rect_StillPaintsTitles()
    {
        // The solver may hand over one row (or none). One row must still show
        // the tabs — just without the underline row.
        var panel = StripPanel(Strip(1, Tab("a", "alpha"), Tab("b", "beta")));
        var buffer = Painted(panel, 40, 1);
        var titles = Row(buffer, 0, 40);
        await Assert.That(titles).Contains("alpha");
        await Assert.That(titles).Contains("beta");
    }

    [Test]
    public async Task ZeroRow_Rect_PaintsNothing()
    {
        // The hidden-strip case: the strip is a split child with ratio 0, so the
        // solver hands it an empty rect. Painting is then a no-op, which is what
        // keeps every pre-#389 golden byte-identical.
        var panel = StripPanel(Strip(0, Tab("a", "alpha"), Tab("b", "beta")));
        var neighbour = new FillPanel("test.fill", minHeight: 4);

        var tree = new LayoutTree();
        tree.AddRoot(neighbour);
        tree.Split(neighbour.Id, SplitDir.Vertical, 0f, panel, gap: 0);
        tree.Solve(40, 4);

        await Assert.That(panel.Rect.Height).IsEqualTo(0);

        var buffer = new ScreenBuffer(40, 4);
        tree.PaintAll(buffer);
        await Assert.That(Row(buffer, 0, 40).Trim()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task VisibleStrip_TakesRows_WithoutMovingTheComposer()
    {
        // The other half of the seating contract: claiming rows has to come out
        // of the timeline, and hiding the strip has to give them back.
        var strip = Strip(0, Tab("a", "alpha"), Tab("b", "beta"));
        var screen = ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);

        screen.SyncTabStrip(TabStripState.Empty, viewportHeight: 24);
        screen.Tree.Solve(80, 24);
        var composerWhenHidden = screen.Composer.Rect;
        await Assert.That(screen.Tabs!.Rect.Height).IsEqualTo(0);

        screen.SyncTabStrip(strip, viewportHeight: 24);
        screen.Tree.Solve(80, 24);

        await Assert.That(screen.Tabs!.Rect.Height).IsEqualTo(SessionTabStripPanel.PreferredRows);
        await Assert.That(screen.Composer.Rect).IsEqualTo(composerWhenHidden);
        // The rows came out of the transcript, which keeps its own minimum.
        await Assert.That(screen.Timeline.Rect.Height).IsGreaterThanOrEqualTo(4);
    }

    [Test]
    public async Task StripNeverSqueezesOutTheTranscript()
    {
        // A terminal too short for timeline + strip + chrome gets no strip, not
        // no chat: the transcript's minimum outranks the strip's wish.
        var screen = ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);
        var strip = Strip(0, Tab("a", "alpha"), Tab("b", "beta"));

        for (int height = 4; height <= 24; height++)
        {
            screen.SyncTabStrip(strip, viewportHeight: height);
            screen.Tree.Solve(80, height);
            await Assert.That(screen.Timeline.Rect.Height).IsGreaterThanOrEqualTo(4);
        }
    }

    private sealed class FillPanel(string id, int minHeight)
        : Panel(id, new Size(0, minHeight), 10)
    {
        public override void Paint(ScreenBuffer buffer) => buffer.Fill(Rect, Cell.Blank);
    }

    // ── titles ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Clean_DropsControlCharsAndTrims()
    {
        await Assert.That(SessionTabStripPanel.Clean("  Chat  ")).IsEqualTo("Chat");
        await Assert.That(SessionTabStripPanel.Clean("a\nb")).IsEqualTo("a b");
        await Assert.That(SessionTabStripPanel.Clean("   ")).IsEqualTo("session");
        await Assert.That(SessionTabStripPanel.Clean(null)).IsEqualTo("session");
    }

    [Test]
    public async Task Truncate_NeverExceedsCellBudget()
    {
        await Assert.That(SessionTabStripPanel.Truncate("hello", 3)).IsEqualTo("hel");
        await Assert.That(SessionTabStripPanel.Truncate("hi", 10)).IsEqualTo("hi");
        await Assert.That(SessionTabStripPanel.Truncate("hi", 0)).IsEqualTo(string.Empty);

        // Wide glyphs are dropped whole rather than split.
        var wide = SessionTabStripPanel.Truncate("日本語", 3);
        await Assert.That(UnicodeWidth.Width(wide.AsSpan())).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task LongTitle_IsTruncatedNotOverflowing()
    {
        var panel = StripPanel(Strip(0, Tab("a", new string('x', 200)), Tab("b", "beta")));
        var plan = panel.Layout(30);
        var cell = plan.Cells[0];
        await Assert.That(cell.Title.Length).IsLessThan(200);
        await Assert.That(UnicodeWidth.Width(cell.Title.AsSpan())).IsLessThanOrEqualTo(cell.TitleCells);
    }

    // ── reducer-only mutations ──────────────────────────────────────────────

    [Test]
    public async Task Mutations_GoThroughDispatchNotLocalState()
    {
        var strip = Strip(0, Tab("a", "alpha"), Tab("b", "beta"));
        var store = new UiStore();
        store.Dispatch(new ChatAppMsg.OpenTab(strip.Tabs[0]));
        store.Dispatch(new ChatAppMsg.OpenTab(strip.Tabs[1]));

        // OpenTab focuses what it opens, so the projected strip is on the
        // SECOND tab — snapshot it here so the final assertion can prove the
        // interactions below moved the store and not the panel's own copy.
        var projected = store.State.Chat.TabStrip;
        var panel = StripPanel(projected);
        var seen = new List<ChatAppMsg>();
        panel.Dispatch = seen.Add;

        var buffer = Painted(panel, 60, 2);
        _ = Row(buffer, 0, 60);

        // A click on the first tab asks the reducer; the panel keeps nothing.
        await Assert.That(panel.ActivateAt(panel.LastPlan.Cells[0].X + 1)).IsTrue();
        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0]).IsTypeOf<ChatAppMsg.ActivateTab>();

        seen.Clear();
        await Assert.That(panel.CloseAt(panel.LastPlan.Cells[1].X + 1)).IsTrue();
        await Assert.That(seen[0]).IsTypeOf<ChatAppMsg.CloseTab>();

        seen.Clear();
        await Assert.That(panel.Next()).IsTrue();
        await Assert.That(seen[0]).IsTypeOf<ChatAppMsg.CycleNextTab>();

        seen.Clear();
        await Assert.That(panel.Previous()).IsTrue();
        await Assert.That(seen[0]).IsTypeOf<ChatAppMsg.CyclePreviousTab>();

        // Nothing moved the panel's own copy: it is still the projected state.
        await Assert.That(panel.Strip.ActiveTabId!.Value).IsEqualTo(projected.ActiveTabId!.Value);
    }

    [Test]
    public async Task NoDispatchSink_IsReadOnlyNotACrash()
    {
        var panel = StripPanel(Strip(0, Tab("a", "alpha"), Tab("b", "beta")));
        _ = Painted(panel, 40, 2);
        await Assert.That(panel.ActivateAt(0)).IsFalse();
        await Assert.That(panel.CloseAt(0)).IsFalse();
        await Assert.That(panel.Next()).IsFalse();
    }

    [Test]
    public async Task HitTest_Miss_ReturnsMinusOne()
    {
        var panel = StripPanel(Strip(0, Tab("a", "alpha"), Tab("b", "beta")));
        _ = Painted(panel, 40, 2);
        var beyond = panel.LastPlan.Cells[^1].X + panel.LastPlan.Cells[^1].Width;
        await Assert.That(panel.HitTest(beyond + 5)).IsEqualTo(-1);
    }
}
