using Harbor.Tui.CellForge.Rendering;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2355: the frame / title / shadow of <see cref="BorderPanel"/> are typed
/// parts. Each owns its state and resolves its own <see cref="CellStyle"/>;
/// the panel root owns the one <see cref="Panel.Rect"/> and only delegates.
/// These tests reach the parts directly (through InternalsVisibleTo), so they
/// pin the discipline rather than only the composed pixels — the composed
/// pixels are already pinned by <c>GoldenGridDumpTests</c> and the existing
/// <c>BorderPanelTests</c>.
/// </summary>
public class BorderPanelPartsTests
{
    // ── style is applied to the part directly, not by a root painter ────

    [Test]
    public async Task BorderFramePart_Focused_ResolvesBoldStyleItself()
    {
        var focused = new BorderFramePart(BorderKind.Plain, focused: true);
        var plain = new BorderFramePart(BorderKind.Plain, focused: false);

        await Assert.That(focused.Style.Attrs).IsEqualTo(StyleAttr.Bold);
        await Assert.That(plain.Style.Attrs).IsEqualTo(StyleAttr.None);
        await Assert.That(plain.Style.Fg).IsEqualTo(PackedColor.Indexed(8));
    }

    [Test]
    public async Task BorderFramePart_Kind_ResolvesItsOwnGlyphs()
    {
        var plain = new BorderFramePart(BorderKind.Plain).Glyphs;
        await Assert.That(plain.TL).IsEqualTo('┌');
        await Assert.That(plain.TR).IsEqualTo('┐');
        await Assert.That(plain.BL).IsEqualTo('└');
        await Assert.That(plain.BR).IsEqualTo('┘');
        await Assert.That(plain.H).IsEqualTo('─');
        await Assert.That(plain.V).IsEqualTo('│');

        var rounded = new BorderFramePart(BorderKind.Rounded).Glyphs;
        await Assert.That(rounded.TL).IsEqualTo('╭');
        await Assert.That(rounded.TR).IsEqualTo('╮');
        await Assert.That(rounded.BL).IsEqualTo('╰');
        await Assert.That(rounded.BR).IsEqualTo('╯');

        var doubled = new BorderFramePart(BorderKind.Double).Glyphs;
        await Assert.That(doubled.TL).IsEqualTo('╔');
        await Assert.That(doubled.BR).IsEqualTo('╝');
        await Assert.That(doubled.H).IsEqualTo('═');
        await Assert.That(doubled.V).IsEqualTo('║');

        var thick = new BorderFramePart(BorderKind.Thick).Glyphs;
        await Assert.That(thick.TL).IsEqualTo('┏');
        await Assert.That(thick.BR).IsEqualTo('┛');
        await Assert.That(thick.H).IsEqualTo('━');
        await Assert.That(thick.V).IsEqualTo('┃');
    }

    [Test]
    public async Task BorderFramePart_PaintsStandalone_FromItsOwnStateAndOneRect()
    {
        // No root, no style argument: the part carries its own state and the
        // caller hands it only geometry. Style and glyphs both come from the
        // part itself.
        var part = new BorderFramePart(BorderKind.Double, focused: true);
        var buf = TestRender.Buffer(6, 4);
        part.Paint(buf, new Rect(0, 0, 6, 4));

        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('╔');
        await Assert.That(buf.Get(5, 0).Rune).IsEqualTo('╗');
        await Assert.That(buf.Get(2, 0).Rune).IsEqualTo('═');
        await Assert.That(buf.Get(0, 1).Rune).IsEqualTo('║');
        await Assert.That(buf.Get(0, 0).Style.Attrs).IsEqualTo(StyleAttr.Bold);
    }

    [Test]
    public async Task BorderTitlePart_Focused_ResolvesBoldStyleItself()
    {
        var focused = new BorderTitlePart("Chat", focused: true);
        var plain = new BorderTitlePart("Chat", focused: false);

        await Assert.That(focused.Style.Attrs).IsEqualTo(StyleAttr.Bold);
        await Assert.That(plain.Style.Attrs).IsEqualTo(StyleAttr.None);
        await Assert.That(plain.Style.Fg).IsEqualTo(PackedColor.Indexed(8));
    }

    [Test]
    public async Task BorderShadowPart_OwnsItsOnOffState()
    {
        var off = new BorderShadowPart(enabled: false);
        var on = new BorderShadowPart(enabled: true);

        await Assert.That(off.Enabled).IsFalse();
        await Assert.That(on.Enabled).IsTrue();
        await Assert.That(on.Style.Bg).IsEqualTo(PackedColor.Indexed(8));
    }

    // ── geometry has one owner: the root's Rect ─────────────────────────

    [Test]
    public async Task BorderPanel_ContentRect_DerivesFromTheOneRootRect()
    {
        var panel = new BorderPanel("p", 4, 2, title: "Chat") { Rect = new Rect(0, 0, 20, 6) };
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(1, 1, 18, 4));

        // Moving the root rect moves content; no part keeps a second geometry.
        panel.Rect = new Rect(3, 5, 10, 4);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(4, 6, 8, 2));
    }

    [Test]
    public async Task BorderPanel_DelegatesStateToParts_PaintingStillComposes()
    {
        var panel = new BorderPanel("p", 4, 2, title: "Chat")
        {
            Rect = new Rect(1, 1, 8, 5),
            BorderKind = BorderKind.Rounded,
            Shadow = true,
            Focused = true,
        };
        var buf = TestRender.Buffer(12, 8);
        panel.Paint(buf);

        // Frame part's glyph set reached the buffer.
        await Assert.That(buf.Get(1, 1).Rune).IsEqualTo('╭');
        // Root synced focus into the parts, so the title painted bold.
        await Assert.That(buf.Get(3, 1).Style.Attrs).IsEqualTo(StyleAttr.Bold);
        // Shadow part's state reached the buffer, outside the frame.
        await Assert.That(buf.Get(9, 2).Style.Bg).IsEqualTo(PackedColor.Indexed(8));
    }
}
