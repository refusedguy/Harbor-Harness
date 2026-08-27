using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class SideBarLayoutTests
{
    private static ChatScreen Build() => ChatScreen.Build(new ComposerController(), new StatusViewModel { Model = "m" });

    [Test]
    public async Task Build_Default_IncludesSidebarPanel()
    {
        var screen = Build();
        await Assert.That(screen.Sidebar).IsNotNull();
        await Assert.That(screen.Tree.Panels.Any(p => p.Id == ChatScreen.SidebarId)).IsTrue();
    }

    [Test]
    public async Task Build_NoSidebar_KeepsLegacyLayout()
    {
        var screen = ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);
        await Assert.That(screen.Sidebar).IsNull();
        await Assert.That(screen.Tree.Panels.Any(p => p.Id == ChatScreen.SidebarId)).IsFalse();
    }

    [Test]
    public async Task Solve_NarrowTerminal_SidebarCollapsed()
    {
        var screen = Build();
        screen.Tree.Solve(40, 24);
        await Assert.That(screen.Sidebar!.Rect.Width).IsEqualTo(0);
        await Assert.That(screen.Timeline.Rect.Width).IsEqualTo(40);
    }

    [Test]
    public async Task Solve_BelowAutoShow_SidebarCollapsed()
    {
        var screen = Build();
        screen.Tree.Solve(119, 30);
        await Assert.That(screen.Sidebar!.Rect.Width).IsEqualTo(0);
    }

    [Test]
    public async Task Solve_WideTerminal_SidebarPinnedAt42()
    {
        var screen = Build();
        screen.Tree.Solve(160, 40);

        await Assert.That(screen.Sidebar!.Rect.Width).IsEqualTo(42);
        await Assert.That(screen.Sidebar.Rect.Right).IsEqualTo(160);
        await Assert.That(screen.Timeline.Rect.Width).IsEqualTo(160 - 42 - 1);
    }

    [Test]
    public async Task Solve_AtAutoShowThreshold_SidebarVisible()
    {
        var screen = Build();
        screen.Tree.Solve(120, 30);
        await Assert.That(screen.Sidebar!.Rect.Width).IsEqualTo(42);
    }

    [Test]
    public async Task Solve_WideTerminal_StatusRowSpansFullWidth()
    {
        var screen = Build();
        screen.Tree.Solve(160, 40);
        await Assert.That(screen.Status.Rect.Width).IsEqualTo(160);
        await Assert.That(screen.Composer.Rect.Width).IsEqualTo(160);
    }

    [Test]
    public async Task Paint_CollapsedSidebar_Skips()
    {
        var screen = Build();
        screen.Tree.Solve(40, 24);
        var buffer = new ScreenBuffer(40, 24);
        screen.Sidebar!.Paint(buffer);
        await Assert.That(screen.Sidebar.Rect.Width).IsEqualTo(0);
    }
}
