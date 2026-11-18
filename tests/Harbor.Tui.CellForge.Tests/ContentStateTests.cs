using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// The content-state contract: one painter for loading, empty and ready. The
// shimmer is a pure function of the host tick, so goldens are style-blind on
// purpose (the glyphs must never move) and the style assertions below pin the
// palette contract. Fixtures live in tests/fixtures/content-state/.
//
// Painting reads the global TerminalColorPalette, so these serialize with
// other paint tests.
[NotInParallel("pty")]
public class ContentStateTests
{
    private static string FixtureDir() => Golden.FixtureDir("content-state");

    private static async Task VerifyGolden(
        string name,
        int cols,
        int rows,
        ContentState state,
        long tick = 0)
    {
        var buffer = TestRender.Buffer(cols, rows);
        state.Paint(buffer, new Rect(0, 0, cols, rows), tick);

        string[] raw = ScreenFixtures.Art(buffer).Split('\n');
        string art = string.Join("\n", Enumerable.Range(0, rows)
            .Select(i => (i < raw.Length ? raw[i] : string.Empty).TrimEnd()));
        string document = $"# golden: {name} ({cols}x{rows})\n" + art + "\n";
        string expected = Golden.VerifyText(FixtureDir(), name, document);
        await Assert.That(expected).IsEqualTo(document);
    }

    [Test]
    public async Task Loading_GoldenPaintsStaircaseSkeleton()
    {
        await VerifyGolden("loading", 16, 4, ContentState.Loading(3));
    }

    [Test]
    public async Task LoadingShimmer_GoldenKeepsGlyphsStill()
    {
        await VerifyGolden("loading-shimmer", 12, 2, ContentState.Loading(2), tick: 3);
    }

    [Test]
    public async Task Empty_GoldenCentersTextAndAction()
    {
        await VerifyGolden("empty", 24, 3,
            ContentState.Empty("No files", "Press Enter to retry"));
    }

    [Test]
    public async Task Ready_GoldenPaintsNothing()
    {
        await VerifyGolden("ready", 12, 2, ContentState.Ready());
    }

    [Test]
    public async Task Factories_ProduceExpectedPresentations()
    {
        await Assert.That(ContentState.Loading().SkeletonRows).IsEqualTo(3);
        await Assert.That(ContentState.Loading(-2).SkeletonRows).IsEqualTo(0);
        await Assert.That(ContentState.Loading().IsLoading).IsTrue();
        await Assert.That(ContentState.Empty().EmptyText).IsEqualTo(ContentState.DefaultEmptyText);
        await Assert.That(ContentState.Empty().IsEmpty).IsTrue();
        await Assert.That(ContentState.Ready().IsReady).IsTrue();
    }

    [Test]
    public async Task RowWidth_FormsAClampedStaircase()
    {
        await Assert.That(ContentSkeleton.RowWidth(16, 0, 3)).IsEqualTo(16);
        await Assert.That(ContentSkeleton.RowWidth(16, 1, 3)).IsEqualTo(12);
        await Assert.That(ContentSkeleton.RowWidth(16, 2, 3)).IsEqualTo(8);
        await Assert.That(ContentSkeleton.RowWidth(3, 0, 3)).IsEqualTo(3);
        await Assert.That(ContentSkeleton.RowWidth(3, 2, 3)).IsEqualTo(2);
        await Assert.That(ContentSkeleton.RowWidth(16, 3, 3)).IsEqualTo(0);
        await Assert.That(ContentSkeleton.RowWidth(0, 0, 3)).IsEqualTo(0);
    }

    [Test]
    public async Task ShimmerSpan_AdvancesOneCellAndWraps()
    {
        (int start, int length) = ContentSkeleton.ShimmerSpan(16, 0);
        await Assert.That(start).IsEqualTo(0);
        await Assert.That(length).IsEqualTo(ContentSkeleton.ShimmerWindow);

        await Assert.That(ContentSkeleton.ShimmerSpan(16, 1).Start).IsEqualTo(1);
        await Assert.That(ContentSkeleton.ShimmerSpan(16, 10).Start).IsEqualTo(10);
        await Assert.That(ContentSkeleton.ShimmerSpan(16, 11).Start).IsEqualTo(0);
        await Assert.That(ContentSkeleton.ShimmerSpan(16, -1).Start).IsEqualTo(10);

        // A row narrower than the window is fully highlighted, never out of bounds.
        (int narrowStart, int narrowLength) = ContentSkeleton.ShimmerSpan(3, 7);
        await Assert.That(narrowStart).IsEqualTo(0);
        await Assert.That(narrowLength).IsEqualTo(3);
        (int zeroStart, int zeroLength) = ContentSkeleton.ShimmerSpan(0, 7);
        await Assert.That(zeroStart).IsEqualTo(0);
        await Assert.That(zeroLength).IsEqualTo(0);
    }

    [Test]
    public async Task Shimmer_RepaintsOnlyTwoCellStylesPerTick()
    {
        var first = TestRender.Buffer(16, 1);
        var second = TestRender.Buffer(16, 1);
        var state = ContentState.Loading(1);
        var rect = new Rect(0, 0, 16, 1);

        state.Paint(first, rect, 0);
        state.Paint(second, rect, 1);

        ContentStateStyle style = ContentStateStyle.Default;
        await Assert.That(first.Get(1, 0).Style).IsEqualTo(style.Shimmer);
        await Assert.That(first.Get(10, 0).Style).IsEqualTo(style.Skeleton);
        await Assert.That(second.Get(6, 0).Style).IsEqualTo(style.Shimmer);
        await Assert.That(second.Get(0, 0).Style).IsEqualTo(style.Skeleton);

        // The sweep changes SGR attributes, not glyphs.
        for (int x = 0; x < 16; x++)
        {
            await Assert.That(first.Get(x, 0).Rune).IsEqualTo((int)ContentSkeleton.Glyph);
            await Assert.That(second.Get(x, 0).Rune).IsEqualTo((int)ContentSkeleton.Glyph);
        }

        int changed = 0;
        for (int x = 0; x < 16; x++)
        {
            if (!first.Get(x, 0).HasSameStyle(second.Get(x, 0)))
            {
                changed++;
            }
        }

        // A one-cell step moves one window edge out and one in; a wrap is
        // still bounded by twice the window, never the whole row.
        await Assert.That(changed).IsEqualTo(2);
        await Assert.That(changed <= 2 * ContentSkeleton.ShimmerWindow).IsTrue();
    }

    [Test]
    public async Task Empty_ClipsLongTextByDisplayWidth()
    {
        var buffer = TestRender.Buffer(8, 2);
        ContentState.Empty("ABCDEFGHIJ").Paint(buffer, new Rect(0, 0, 8, 2), 0);

        await Assert.That(ScreenFixtures.Row(buffer, 0, 8)).IsEqualTo("ABCDEFGH");
        await Assert.That(ScreenFixtures.Row(buffer, 1, 8)).IsEqualTo("        ");
    }

    [Test]
    public async Task Empty_UsesDefaultTextWhenNoneIsSupplied()
    {
        var buffer = TestRender.Buffer(20, 1);
        ContentState.Empty().Paint(buffer, new Rect(0, 0, 20, 1), 0);

        await Assert.That(ScreenFixtures.Row(buffer, 0, 20))
            .IsEqualTo("  Nothing here yet  ");
    }

    [Test]
    public async Task Empty_ShowsActionOnlyOnTheSecondRow()
    {
        var shortBuffer = TestRender.Buffer(20, 1);
        ContentState.Empty("Hi", "Retry").Paint(shortBuffer, new Rect(0, 0, 20, 1), 0);
        await Assert.That(ScreenFixtures.Row(shortBuffer, 0, 20))
            .IsEqualTo("         Hi         ");

        var tallBuffer = TestRender.Buffer(20, 2);
        ContentState.Empty("Hi", "Retry").Paint(tallBuffer, new Rect(0, 0, 20, 2), 0);
        await Assert.That(ScreenFixtures.Row(tallBuffer, 1, 20))
            .IsEqualTo("       Retry        ");
    }

    [Test]
    public async Task ReadyAndEmptyRect_PaintNothing()
    {
        var ready = TestRender.Buffer(8, 2);
        ContentState.Ready().Paint(ready, new Rect(0, 0, 8, 2), 0);
        await Assert.That(ScreenFixtures.Row(ready, 0, 8)).IsEqualTo("        ");
        await Assert.That(ScreenFixtures.Row(ready, 1, 8)).IsEqualTo("        ");

        var narrow = TestRender.Buffer(8, 2);
        ContentState.Loading(3).Paint(narrow, new Rect(0, 0, 0, 2), 0);
        await Assert.That(ScreenFixtures.Row(narrow, 0, 8)).IsEqualTo("        ");

        var flat = TestRender.Buffer(8, 2);
        ContentState.Loading(3).Paint(flat, new Rect(0, 0, 8, 0), 0);
        await Assert.That(ScreenFixtures.Row(flat, 0, 8)).IsEqualTo("        ");
    }

    [Test]
    public async Task ExplicitStyleContract_OverridesPaletteDefaults()
    {
        var custom = new ContentStateStyle(
            new CellStyle(PackedColor.Rgb(1, 2, 3)),
            new CellStyle(PackedColor.Rgb(4, 5, 6)),
            new CellStyle(PackedColor.Rgb(7, 8, 9)),
            new CellStyle(PackedColor.Rgb(10, 11, 12)));

        var buffer = TestRender.Buffer(16, 1);
        ContentState.Loading(1).Paint(buffer, new Rect(0, 0, 16, 1), 0, custom);

        await Assert.That(buffer.Get(10, 0).Style).IsEqualTo(custom.Skeleton);
        await Assert.That(buffer.Get(0, 0).Style).IsEqualTo(custom.Shimmer);
        await Assert.That(buffer.Get(1, 0).Style).IsEqualTo(custom.Shimmer);
        await Assert.That(custom.Resolve()).IsEqualTo(custom);
        await Assert.That(default(ContentStateStyle).Resolve()).IsEqualTo(ContentStateStyle.Default);
    }
}
