// #2696 item 12: the shadow post effect must be a pure per-cell transform,
// must respect the dialog/split of the shifted rect, and must compose with the
// existing fixed-slot pipeline without changing its bookkeeping.
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class ShadowEffectTests
{
    private static readonly EngineCells.Rect Dialog = new(10, 5, 20, 8);

    private static EngineCells.Cell Colored(char rune, EngineCells.PackedColor fg, EngineCells.PackedColor bg) =>
        EngineCells.Cell.From(
            new Rune(rune),
            new EngineCells.CellStyle(fg, bg, EngineCells.StyleAttr.Bold));

    private static ShadowEffect Armed(double strength = 0.5)
    {
        var effect = new ShadowEffect();
        effect.Update(Dialog, offsetX: 2, offsetY: 1, strength);
        return effect;
    }

    [Test]
    public async Task Shadow_Darkens_Offset_Band_And_Leaves_Dialog_Alone()
    {
        var effect = Armed();
        var cell = Colored(
            'x',
            EngineCells.PackedColor.Rgb(200, 100, 50),
            EngineCells.PackedColor.Rgb(80, 40, 20));

        // Dialog spans x 10..29, y 5..12; the shifted band adds x 30..31 and
        // y 13, and the dialog cells inside that band must stay untouched.
        var shadowed = effect.Transform(30, 6, in cell);
        var (r, g, b) = shadowed.Style.Fg.RgbChannels;
        var (br, bg, bb) = shadowed.Style.Bg.RgbChannels;

        await Assert.That(shadowed.Rune).IsEqualTo(cell.Rune);
        await Assert.That(r).IsEqualTo((byte)100);
        await Assert.That(g).IsEqualTo((byte)50);
        await Assert.That(b).IsEqualTo((byte)25);
        await Assert.That(br).IsEqualTo((byte)40);
        await Assert.That(bg).IsEqualTo((byte)20);
        await Assert.That(bb).IsEqualTo((byte)10);
        await Assert.That((int)shadowed.Style.Attrs)
            .IsEqualTo((int)(EngineCells.StyleAttr.Bold | EngineCells.StyleAttr.Dim));

        await Assert.That(effect.Transform(20, 6, in cell)).IsEqualTo(cell);
        await Assert.That(effect.Transform(35, 6, in cell)).IsEqualTo(cell);
        await Assert.That(effect.Transform(15, 20, in cell)).IsEqualTo(cell);
    }

    [Test]
    public async Task Shadow_Zero_Strength_Is_Identity()
    {
        var effect = Armed(strength: 0.0);
        var cell = Colored('x', EngineCells.PackedColor.Rgb(200, 100, 50), EngineCells.PackedColor.Rgb(80, 40, 20));

        await Assert.That(effect.Region).IsEqualTo(new EngineCells.Rect(12, 6, 20, 8));
        await Assert.That(effect.Transform(30, 6, in cell)).IsEqualTo(cell);
    }

    [Test]
    public async Task Shadow_Indexed_Color_Moves_Toward_Black_Default_Stays_Default()
    {
        var effect = Armed();
        var indexed = Colored('x', EngineCells.PackedColor.Indexed(200), EngineCells.PackedColor.Indexed(20));
        var shadowed = effect.Transform(30, 6, in indexed);

        await Assert.That(shadowed.Style.Fg.Index).IsEqualTo((byte)100);
        await Assert.That(shadowed.Style.Bg.Index).IsEqualTo((byte)10);

        var plain = EngineCells.Cell.From(new Rune(' '), EngineCells.CellStyle.Plain);
        var shadowedPlain = effect.Transform(30, 6, in plain);
        await Assert.That(shadowedPlain.Style.Fg.IsDefault).IsTrue();
        await Assert.That(shadowedPlain.Style.Bg.IsDefault).IsTrue();
        await Assert.That((int)shadowedPlain.Style.Attrs).IsEqualTo((int)EngineCells.StyleAttr.Dim);
    }

    [Test]
    public async Task Shadow_Composes_With_The_Fixed_Slot_Pipeline()
    {
        var effect = Armed();
        var pipeline = new PostFxPipeline();
        pipeline.Set(0, effect);
        var cell = Colored('x', EngineCells.PackedColor.Rgb(200, 100, 50), EngineCells.PackedColor.Rgb(80, 40, 20));

        await Assert.That(pipeline.Count).IsEqualTo(1);
        await Assert.That(pipeline.Transform(30, 6, in cell)).IsEqualTo(effect.Transform(30, 6, in cell));

        pipeline.Clear();
        await Assert.That(pipeline.Count).IsEqualTo(0);
        await Assert.That(pipeline.Transform(30, 6, in cell)).IsEqualTo(cell);
    }
}
