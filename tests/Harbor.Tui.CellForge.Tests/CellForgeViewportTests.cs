using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

public class CellForgeViewportTests
{
    private static UiScreenModel Screen(int lineCount)
    {
        var lines = new UiRenderedLine[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            lines[i] = new UiRenderedLine($"l{i}", Array.Empty<StyledSpan>(), UiLineKind.Body, DateTime.UtcNow);
        }

        var transcript = new UiTranscriptModel(Array.Empty<UiBlock>(), lines, null);
        var header = new UiHeaderModel(
            string.Empty, string.Empty, string.Empty,
            IsAgentRunning: false, IsStreaming: false, ShouldQuit: false,
            new CostSnapshot(0, 0, 0m), string.Empty);
        return new UiScreenModel(
            header,
            transcript,
            new UiStatusBarModel(Array.Empty<UiStatusSegment>()),
            new UiInputModel(string.Empty, 0, IsEnabled: true, string.Empty),
            FocusMode.Input,
            "rev");
    }

    [Test]
    public async Task Ctor_StoresDims_PinnedToTail()
    {
        var viewport = new CellForgeViewport(100, 30);
        await Assert.That(viewport.Width).IsEqualTo(100);
        await Assert.That(viewport.Height).IsEqualTo(30);
        await Assert.That(viewport.ViewportLines).IsEqualTo(30);
        await Assert.That(viewport.ScrollOffset).IsEqualTo(0);
        await Assert.That(viewport.TotalLines).IsEqualTo(0);
        await Assert.That(viewport.FollowTail).IsTrue();
    }

    [Test]
    public async Task Ctor_NonPositiveDims_FallBackTo80x24()
    {
        var zero = new CellForgeViewport(0, 0);
        await Assert.That((zero.Width, zero.Height)).IsEqualTo((80, 24));

        var negative = new CellForgeViewport(-10, -5);
        await Assert.That((negative.Width, negative.Height)).IsEqualTo((80, 24));

        var mixed = new CellForgeViewport(0, 50);
        await Assert.That((mixed.Width, mixed.Height)).IsEqualTo((80, 50));
    }

    [Test]
    public async Task FromConsole_ReturnsPositiveDims()
    {
        var viewport = CellForgeViewport.FromConsole();
        await Assert.That(viewport.Width).IsGreaterThan(0);
        await Assert.That(viewport.Height).IsGreaterThan(0);
    }

    [Test]
    public async Task Apply_EmptyTranscript_ZeroesTotals()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Apply(Screen(0));
        await Assert.That(viewport.TotalLines).IsEqualTo(0);
        await Assert.That(viewport.MaxScrollOffset).IsEqualTo(0);
        await Assert.That(viewport.ScrollOffset).IsEqualTo(0);
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(0);
        await Assert.That(viewport.VisibleRowCount).IsEqualTo(0);
        await Assert.That(viewport.ScrollPercent).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_FollowTail_SnapsToLiveEnd()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.SetViewportLines(10);
        viewport.Apply(Screen(30));
        await Assert.That(viewport.TotalLines).IsEqualTo(30);
        await Assert.That(viewport.FollowTail).IsTrue();
        await Assert.That(viewport.ScrollOffset).IsEqualTo(0);
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(20);
        await Assert.That(viewport.VisibleRowCount).IsEqualTo(10);
    }

    [Test]
    public async Task Apply_Unpinned_ClampsOffsetToNewMax()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.SetViewportLines(10);
        viewport.Apply(Screen(30));
        viewport.ScrollBy(-100); // offset 20 (max), unpinned
        viewport.Apply(Screen(12)); // max shrinks to 2
        await Assert.That(viewport.FollowTail).IsFalse();
        await Assert.That(viewport.ScrollOffset).IsEqualTo(2);
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_IsZeroAlloc_ReadsOnlyCount()
    {
        var viewport = new CellForgeViewport(80, 24);
        var screen = Screen(50);
        viewport.Apply(screen); // warmup (JIT)
        long before = GC.GetAllocatedBytesForCurrentThread();
        viewport.Apply(screen);
        long after = GC.GetAllocatedBytesForCurrentThread();
        await Assert.That(after == before).IsTrue();
    }

    [Test]
    public async Task ScrollBy_Up_UnpinsFollowTail()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Apply(Screen(30)); // max = 30 - 24 = 6
        viewport.ScrollBy(-3);
        await Assert.That(viewport.FollowTail).IsFalse();
        await Assert.That(viewport.ScrollOffset).IsEqualTo(3);
    }

    [Test]
    public async Task ScrollBy_Down_DoesNotRepin()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Apply(Screen(30));
        viewport.ScrollBy(-4);
        viewport.ScrollBy(2); // toward the tail — still unpinned
        await Assert.That(viewport.FollowTail).IsFalse();
        await Assert.That(viewport.ScrollOffset).IsEqualTo(2);
    }

    [Test]
    public async Task ScrollBy_ClampsToBothEnds()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Apply(Screen(30)); // max 6
        viewport.ScrollBy(-10000);
        await Assert.That(viewport.ScrollOffset).IsEqualTo(6);
        viewport.ScrollBy(10000);
        await Assert.That(viewport.ScrollOffset).IsEqualTo(0);
        await Assert.That(viewport.FollowTail).IsFalse(); // clamping never re-pins
    }

    [Test]
    public async Task ScrollToTop_MovesToMax_AndUnpins()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Apply(Screen(30)); // max 6
        viewport.ScrollToTop();
        await Assert.That(viewport.FollowTail).IsFalse();
        await Assert.That(viewport.ScrollOffset).IsEqualTo(6);
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(0);
    }

    [Test]
    public async Task ScrollToEnd_Repins_AndZeroesOffset()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Apply(Screen(30));
        viewport.ScrollToTop();
        viewport.ScrollToEnd();
        await Assert.That(viewport.FollowTail).IsTrue();
        await Assert.That(viewport.ScrollOffset).IsEqualTo(0);
    }

    [Test]
    public async Task Resize_AppliesDims_AndNormalizesNonPositive()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.Resize(100, 30);
        await Assert.That((viewport.Width, viewport.Height)).IsEqualTo((100, 30));

        viewport.Resize(0, -5);
        await Assert.That((viewport.Width, viewport.Height)).IsEqualTo((80, 24));

        viewport.Resize(-1, 40);
        await Assert.That((viewport.Width, viewport.Height)).IsEqualTo((80, 40));
    }

    [Test]
    public async Task SetViewportLines_UpdatesMax_AndClampsOffset()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.SetViewportLines(10);
        viewport.Apply(Screen(30));
        viewport.ScrollBy(-100); // offset 20
        viewport.SetViewportLines(25); // max shrinks to 5
        await Assert.That(viewport.ViewportLines).IsEqualTo(25);
        await Assert.That(viewport.MaxScrollOffset).IsEqualTo(5);
        await Assert.That(viewport.ScrollOffset).IsEqualTo(5);

        viewport.SetViewportLines(-3); // negative clamps to zero, everything fits
        await Assert.That(viewport.ViewportLines).IsEqualTo(0);
        await Assert.That(viewport.MaxScrollOffset).IsEqualTo(30);
    }

    [Test]
    public async Task RefreshFromConsole_KeepsPositiveDims()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.RefreshFromConsole();
        await Assert.That(viewport.Width).IsGreaterThan(0);
        await Assert.That(viewport.Height).IsGreaterThan(0);
    }

    [Test]
    public async Task ScrollPercent_MirrorsUiStateFormula()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.SetViewportLines(10);
        viewport.Apply(Screen(30)); // max 20
        await Assert.That(viewport.ScrollPercent).IsEqualTo(0); // pinned = live

        viewport.ScrollBy(-10); // offset 10 of 20
        await Assert.That(viewport.ScrollPercent).IsEqualTo(50);

        viewport.ScrollToTop();
        await Assert.That(viewport.ScrollPercent).IsEqualTo(100);

        viewport.Apply(Screen(5)); // everything fits
        await Assert.That(viewport.ScrollPercent).IsEqualTo(0);
    }

    [Test]
    public async Task FirstVisibleRow_And_VisibleRowCount()
    {
        var viewport = new CellForgeViewport(80, 24);
        viewport.SetViewportLines(10);
        viewport.Apply(Screen(30));
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(20);
        await Assert.That(viewport.VisibleRowCount).IsEqualTo(10);

        viewport.ScrollBy(-5);
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(15);

        viewport.Apply(Screen(4)); // short history: clamped, all rows visible
        await Assert.That(viewport.FirstVisibleRow).IsEqualTo(0);
        await Assert.That(viewport.VisibleRowCount).IsEqualTo(4);
    }
}
