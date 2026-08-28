using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class UnicodeWidthTests
{
    [Test]
    public async Task Width_Ascii_IsOne() => await Assert.That(UnicodeWidth.Width((Rune)'a')).IsEqualTo(1);

    [Test]
    public async Task Width_Cyrillic_IsOne() => await Assert.That(UnicodeWidth.Width((Rune)'я')).IsEqualTo(1);

    [Test]
    public async Task Width_Cjk_IsTwo()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x4E2D))).IsEqualTo(2); // 中
        await Assert.That(UnicodeWidth.Width(new Rune(0xAC00))).IsEqualTo(2); // 가
        await Assert.That(UnicodeWidth.Width(new Rune(0x30A2))).IsEqualTo(2); // ア
    }

    [Test]
    public async Task Width_EmojiBase_IsTwo()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x1F600))).IsEqualTo(2); // grinning face
        await Assert.That(UnicodeWidth.Width(new Rune(0x1F44D))).IsEqualTo(2); // thumbs up
    }

    [Test]
    public async Task Width_Vs16_AndCombining_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0xFE0F))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x0301))).IsEqualTo(0); // combining acute
        await Assert.That(UnicodeWidth.Width(new Rune(0x200D))).IsEqualTo(0); // ZWJ
    }

    [Test]
    public async Task Width_ControlChars_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x07))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x7F))).IsEqualTo(0);
    }

    [Test]
    public async Task Width_Span_SumsRunes()
    {
        const string text = "привет中"; // 6 + 2
        await Assert.That(UnicodeWidth.Width(text)).IsEqualTo(8);
    }
}

public class PackedColorTests
{
    [Test]
    public async Task Default_HasDefaultBit()
    {
        var c = PackedColor.Default;
        await Assert.That(c.IsDefault).IsTrue();
        await Assert.That(PackedColor.Default == default).IsTrue();
    }

    [Test]
    public async Task Indexed_RoundTrips()
    {
        var c = PackedColor.Indexed(196);
        await Assert.That(c.IsDefault).IsFalse();
        await Assert.That(c.IsRgb).IsFalse();
        int index = c.Index;
        await Assert.That(index).IsEqualTo(196);
    }

    [Test]
    public async Task Rgb_RoundTrips()
    {
        var c = PackedColor.Rgb(10, 128, 255);
        await Assert.That(c.IsRgb).IsTrue();
        var channels = c.RgbChannels;
        int r = channels.R, g = channels.G, b = channels.B;
        await Assert.That(r).IsEqualTo(10);
        await Assert.That(g).IsEqualTo(128);
        await Assert.That(b).IsEqualTo(255);
    }

    [Test]
    public async Task Equality_IsValueBased()
    {
        await Assert.That(PackedColor.Rgb(1, 2, 3).Equals(PackedColor.Rgb(1, 2, 3))).IsTrue();
        await Assert.That(PackedColor.Indexed(5) != PackedColor.Indexed(6)).IsTrue();
    }

    [Test]
    public async Task CellStyle_Plain_IsAllDefault()
    {
        await Assert.That(CellStyle.Plain.IsPlain).IsTrue();
        await Assert.That(default(CellStyle).IsPlain).IsTrue();
    }
}
