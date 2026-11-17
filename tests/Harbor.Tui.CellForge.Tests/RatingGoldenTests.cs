using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// The Rating primitive's contract: external state, keyboard selection, a
// read-only painter, and pinned grid art. Goldens are style-blind on purpose —
// the painted GLYPHS are what a user reads; styles are checked by the paint
// assertions below. Fixtures live in tests/fixtures/rating/<name>.golden.txt
// and are seeded with HARBOR_UPDATE_GOLDENS=1 (or the goldens workflow).
//
// Painting reads the global TerminalColorPalette — serialized with other paint tests.
[NotInParallel("pty")]
public class RatingGoldenTests
{
    private static string FixtureDir() => Golden.FixtureDir("rating");

    private static async Task VerifyGolden(string name, Rating rating, int cols = 16, int rows = 2)
    {
        var buffer = TestRender.Buffer(cols, rows);
        rating.Paint(buffer, new Rect(0, 0, cols, rows));

        string[] raw = ScreenFixtures.Art(buffer).Split('\n');
        var lines = new string[rows];
        for (int i = 0; i < rows; i++)
        {
            lines[i] = (i < raw.Length ? raw[i] : string.Empty).TrimEnd();
        }

        string art = string.Join("\n", lines);
        string document = $"# golden: {name} ({cols}x{rows})\n" + art + "\n";
        string expected = Golden.VerifyText(FixtureDir(), name, document);
        await Assert.That(expected).IsEqualTo(document);
    }

    [Test]
    public async Task Empty_ShowsAllHollowStars()
    {
        await VerifyGolden("empty", new Rating(0));
    }

    [Test]
    public async Task Partial_ShowsFilledThenHollowStars()
    {
        await VerifyGolden("partial", new Rating(3));
    }

    [Test]
    public async Task Full_ShowsAllFilledStars()
    {
        await VerifyGolden("full", new Rating(5));
    }

    [Test]
    public async Task ReadOnly_MarksTheLabel()
    {
        await VerifyGolden("readonly", new Rating(new RatingState(3, 5, readOnly: true)));
    }

    [Test]
    public async Task Keyboard_SelectionChangesThePaintedStars()
    {
        var rating = new Rating(2);
        _ = rating.HandleKey(KeyEvent.Simple(KeyCode.Right));
        _ = rating.HandleKey(KeyEvent.Simple(KeyCode.Right));

        await VerifyGolden("keyboard", rating);
    }

    [Test]
    public async Task NarrowRect_PaintsStarsOnly()
    {
        await VerifyGolden("narrow", new Rating(3), cols: 5);
    }

    [Test]
    public async Task OversizedState_ClampsToNineStars()
    {
        await VerifyGolden("clamped", new Rating(new RatingState(20, 20)));
    }

    [Test]
    public async Task ExternalState_IsClampedAndCopyable()
    {
        var state = new RatingState(20, 20);
        await Assert.That(state.Maximum).IsEqualTo(RatingState.MaxStars);
        await Assert.That(state.Value).IsEqualTo(RatingState.MaxStars);

        var zeroMax = new RatingState(3, 0);
        await Assert.That(zeroMax.Maximum).IsEqualTo(1);
        await Assert.That(zeroMax.Value).IsEqualTo(1);

        RatingState edited = state.WithValue(2).WithReadOnly(true);
        await Assert.That(edited.Value).IsEqualTo(2);
        await Assert.That(edited.Maximum).IsEqualTo(RatingState.MaxStars);
        await Assert.That(edited.ReadOnly).IsTrue();
    }

    [Test]
    public async Task Keyboard_ArrowsVimAndDigitsSelectValues()
    {
        var rating = new Rating(2);

        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(3);
        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(2);

        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('k')))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(3);
        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('j')))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(2);

        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.Home))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(1);
        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.End))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(5);

        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('4')))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(4);
        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('0')))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(0);

        // A digit outside the scale is left to the host, not silently clamped.
        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('9')))).IsFalse();
        await Assert.That(rating.State.Value).IsEqualTo(0);
    }

    [Test]
    public async Task Edges_AreConsumedWithoutChangingValue()
    {
        var empty = new Rating(0);
        await Assert.That(empty.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(empty.HandleKey(KeyEvent.Char(new Rune('j')))).IsTrue();
        await Assert.That(empty.State.Value).IsEqualTo(0);

        var full = new Rating(5);
        await Assert.That(full.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(full.HandleKey(KeyEvent.Char(new Rune('l')))).IsTrue();
        await Assert.That(full.State.Value).IsEqualTo(5);
    }

    [Test]
    public async Task ReadOnly_RefusesEveryKey()
    {
        var rating = new Rating(new RatingState(3, 5, readOnly: true));

        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsFalse();
        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('1')))).IsFalse();
        await Assert.That(rating.State.Value).IsEqualTo(3);
    }

    [Test]
    public async Task KeyGate_RefusesModifiedAndReleaseKeys()
    {
        var rating = new Rating(2);

        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('l'), KeyModifiers.Shift))).IsFalse();
        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('l'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.Right, KeyModifiers.Alt))).IsFalse();
        await Assert.That(rating.HandleKey(new KeyEvent(
            KeyCode.Right, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();
        await Assert.That(rating.State.Value).IsEqualTo(2);

        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('l')))).IsTrue();
        await Assert.That(rating.State.Value).IsEqualTo(3);
    }

    [Test]
    public async Task ValueChanged_ReportsOldAndNewValue()
    {
        var rating = new Rating(2);
        var seen = new List<RatingChange>();
        rating.ValueChanged += (_, change) => seen.Add(change);

        await Assert.That(rating.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(rating.HandleKey(KeyEvent.Char(new Rune('5')))).IsTrue();

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0]).IsEqualTo(new RatingChange(2, 3));
        await Assert.That(seen[1]).IsEqualTo(new RatingChange(3, 5));
    }

    [Test]
    public async Task HostCanOverrideSelectionThroughExternalState()
    {
        var rating = new Rating(2);
        _ = rating.HandleKey(KeyEvent.Simple(KeyCode.Right));
        await Assert.That(rating.State.Value).IsEqualTo(3);

        rating.SetState(new RatingState(1, 5));
        await Assert.That(rating.State.Value).IsEqualTo(1);

        rating.SetValue(4);
        await Assert.That(rating.State.Value).IsEqualTo(4);
    }

    [Test]
    public async Task Paint_StylesFilledStarsDifferentlyFromEmpty()
    {
        var buffer = TestRender.Buffer(16, 2);
        new Rating(3).Paint(buffer, new Rect(0, 0, 16, 2));

        var filled = buffer.Get(0, 0);
        var empty = buffer.Get(3, 0);
        await Assert.That(filled.Rune).IsEqualTo((int)Rating.FilledStar);
        await Assert.That(empty.Rune).IsEqualTo((int)Rating.EmptyStar);
        await Assert.That(filled.HasSameStyle(empty)).IsFalse();

        var readOnlyBuffer = TestRender.Buffer(16, 2);
        new Rating(new RatingState(3, 5, readOnly: true)).Paint(readOnlyBuffer, new Rect(0, 0, 16, 2));
        await Assert.That(readOnlyBuffer.Get(0, 0).HasSameStyle(filled)).IsFalse();
    }
}
