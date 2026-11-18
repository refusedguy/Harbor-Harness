using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// The compact-label family contract (#2371): four variants share one style
// contract, the content is external, and the painted art is pinned by goldens.
// Goldens are style-blind on purpose. The GLYPHS are what a user reads, and
// the style assertions below check the palette mapping. Fixtures live in
// tests/fixtures/compact-label/<name>.golden.txt and are seeded with
// HARBOR_UPDATE_GOLDENS=1 (or the goldens workflow).
//
// Painting reads the global TerminalColorPalette, so this test is serialized
// with the other paint tests.
[NotInParallel("pty")]
public class CompactLabelTests
{
    private static string FixtureDir() => Golden.FixtureDir("compact-label");

    private static async Task VerifyGolden(string name, CompactLabel label, int cols = 16)
    {
        var buffer = TestRender.Buffer(cols, 2);
        label.Paint(buffer, new Rect(0, 0, cols, 2));

        string art = ScreenFixtures.Art(buffer).Split('\n')[0].TrimEnd();
        string document = $"# golden: {name} ({cols}x2)\n{art}\n\n";
        await Assert.That(Golden.VerifyText(FixtureDir(), name, document)).IsEqualTo(document);
    }

    [Test]
    public async Task Badge_ShowsTheHostCount()
    {
        await VerifyGolden("badge", new CompactLabel(CompactLabelVariant.Badge, "ignored", count: 3));
    }

    [Test]
    public async Task Badge_FallsBackToTextWhenNoCountIsSupplied()
    {
        await VerifyGolden("badge-text", new CompactLabel(CompactLabelVariant.Badge, "new"));
    }

    [Test]
    public async Task Tag_ShowsItsKeyword()
    {
        await VerifyGolden("tag", new CompactLabel(CompactLabelVariant.Tag, "work"));
    }

    [Test]
    public async Task DismissibleTag_ShowsTheCloseGlyph()
    {
        await VerifyGolden("tag-dismiss", new CompactLabel(CompactLabelVariant.Tag, "work", dismissible: true));
    }

    [Test]
    public async Task Kbd_CapsTheHotkey()
    {
        await VerifyGolden("kbd", new CompactLabel(CompactLabelVariant.Kbd, "Ctrl+K"));
    }

    [Test]
    public async Task Marker_LeadsTheStatusString()
    {
        await VerifyGolden("marker", new CompactLabel(CompactLabelVariant.Marker, "loading"));
    }

    [Test]
    public async Task NarrowRect_ClipsTheLabel()
    {
        await VerifyGolden("clipped", new CompactLabel(CompactLabelVariant.Kbd, "Ctrl+K"), cols: 5);
    }

    [Test]
    public async Task StyleContract_GivesEveryVariantItsOwnBodyStyle()
    {
        var badge = CompactLabelStyle.For(CompactLabelVariant.Badge);
        var tag = CompactLabelStyle.For(CompactLabelVariant.Tag);
        var kbd = CompactLabelStyle.For(CompactLabelVariant.Kbd);
        var marker = CompactLabelStyle.For(CompactLabelVariant.Marker);

        await Assert.That(badge.Body).IsNotEqualTo(tag.Body);
        await Assert.That(tag.Body).IsNotEqualTo(kbd.Body);
        await Assert.That(kbd.Body).IsNotEqualTo(marker.Body);
        await Assert.That(marker.Body).IsNotEqualTo(badge.Body);

        await Assert.That(badge.Lead).IsEqualTo('[');
        await Assert.That(badge.Tail).IsEqualTo(']');
        await Assert.That(tag.Lead).IsEqualTo('<');
        await Assert.That(tag.Tail).IsEqualTo('>');
        await Assert.That(marker.Lead).IsEqualTo('●');
        await Assert.That(marker.Tail).IsEqualTo('\0');

        var label = new CompactLabel(CompactLabelVariant.Tag, "work");
        await Assert.That(label.Style).IsEqualTo(CompactLabelStyle.For(CompactLabelVariant.Tag));
    }

    [Test]
    public async Task State_NormalizesFieldsThatTheVariantDoesNotUse()
    {
        var badge = new CompactLabelState(CompactLabelVariant.Badge, "x", count: 2, dismissible: true);
        await Assert.That(badge.Count).IsEqualTo(2);
        await Assert.That(badge.Dismissible).IsFalse();

        var tag = new CompactLabelState(CompactLabelVariant.Tag, "x", count: 2, dismissible: true);
        await Assert.That(tag.Count).IsNull();
        await Assert.That(tag.Dismissible).IsTrue();

        CompactLabelState moved = badge.WithVariant(CompactLabelVariant.Tag);
        await Assert.That(moved.Count).IsNull();
        await Assert.That(moved.Variant).IsEqualTo(CompactLabelVariant.Tag);

        var nullText = new CompactLabelState(CompactLabelVariant.Marker, null);
        await Assert.That(nullText.Text).IsEqualTo(string.Empty);
        await Assert.That(nullText.WithText("ready").Text).IsEqualTo("ready");
    }

    [Test]
    public async Task DismissibleTag_PaintsTheCloseGlyphDim()
    {
        var buffer = TestRender.Buffer(16, 2);
        new CompactLabel(CompactLabelVariant.Tag, "work", dismissible: true)
            .Paint(buffer, new Rect(0, 0, 16, 2));

        var body = buffer.Get(0, 0);
        var close = buffer.Get(6, 0); // '<' + "work" + ' ' => index 6
        await Assert.That(close.Rune).IsEqualTo((int)CompactLabelStyle.DismissGlyph);
        await Assert.That(body.HasSameStyle(close)).IsFalse();
    }

    [Test]
    public async Task Paint_UsesTheStyleContractForTheBody()
    {
        var buffer = TestRender.Buffer(16, 2);
        new CompactLabel(CompactLabelVariant.Kbd, "Ctrl+K").Paint(buffer, new Rect(0, 0, 16, 2));

        var lead = buffer.Get(0, 0);
        var body = buffer.Get(1, 0);
        await Assert.That(lead.Rune).IsEqualTo((int)'[');
        await Assert.That(body.Rune).IsEqualTo((int)'C');
        await Assert.That(lead.HasSameStyle(body)).IsTrue();
    }

    [Test]
    public async Task EmptyRect_PaintsNothing()
    {
        var buffer = TestRender.Buffer(8, 2);
        new CompactLabel(CompactLabelVariant.Badge, "x", count: 1)
            .Paint(buffer, new Rect(0, 0, 0, 2));
        new CompactLabel(CompactLabelVariant.Marker, "x")
            .Paint(buffer, new Rect(0, 1, 8, 0));

        await Assert.That(ScreenFixtures.Art(buffer)).IsEqualTo("        \n        ");
    }

    [Test]
    public async Task SetState_ReplacesTheExternalContent()
    {
        var label = new CompactLabel(CompactLabelVariant.Badge, "old", count: 1);
        label.SetState(new CompactLabelState(CompactLabelVariant.Marker, "ready"));

        await Assert.That(label.State.Variant).IsEqualTo(CompactLabelVariant.Marker);
        await Assert.That(label.State.Text).IsEqualTo("ready");

        var buffer = TestRender.Buffer(16, 2);
        label.Paint(buffer, new Rect(0, 0, 16, 2));
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'●');
    }
}
