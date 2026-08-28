using Harbor.Ui.Framework.Projection;

namespace Harbor.DesignSystem.Tests;

/// <summary>
/// Canonical theme-JSON codec: hex parsing, full/partial catalogs merged over
/// a fallback, fatal validation (malformed JSON, bad hex) without throwing,
/// and non-fatal lint (unknown properties, WCAG contrast).
/// </summary>
public class ThemeJsonTests
{
    [Test]
    public async Task Parse_FullCatalog_ProducesNamedTheme()
    {
        string json = """
            {
              "name": "sunset",
              "accent": "#ff8800",
              "text": "#ffffff",
              "background": "#10080a"
            }
            """;

        var result = ThemeJson.Parse(json, HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme.Name).IsEqualTo("sunset");
        await Assert.That(result.Theme.Accent).IsEqualTo(new RgbColor(0xFF, 0x88, 0x00));
        await Assert.That(result.Theme.Text).IsEqualTo(new RgbColor(0xFF, 0xFF, 0xFF));
        await Assert.That(result.Theme.Background).IsEqualTo(new RgbColor(0x10, 0x08, 0x0A));
        // omitted slots merge over the fallback
        await Assert.That(result.Theme.Tool).IsEqualTo(HarborTheme.HarborDark.Tool);
    }

    [Test]
    public async Task Parse_Partial_MergesOverFallback()
    {
        var result = ThemeJson.Parse("""{ "name": "cool-plus", "accent": "#123456" }""", HarborTheme.HarborCool);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme.Accent).IsEqualTo(new RgbColor(0x12, 0x34, 0x56));
        await Assert.That(result.Theme.Text).IsEqualTo(HarborTheme.HarborCool.Text);
        await Assert.That(result.Theme.Background).IsEqualTo(HarborTheme.HarborCool.Background);
    }

    [Test]
    public async Task Parse_InvalidHex_CollectsAllSlotErrors()
    {
        var result = ThemeJson.Parse("""{ "accent": "#nope", "text": "zzz" }""", HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Errors).Count().IsEqualTo(2);
        await Assert.That(result.Error).Contains("accent");
        await Assert.That(result.Error).Contains("text");
    }

    [Test]
    public async Task Parse_MalformedJson_FailsWithoutThrowing()
    {
        var result = ThemeJson.Parse("{ not json ", HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("theme JSON invalid");
    }

    [Test]
    public async Task Parse_EmptyObject_DefaultsNameAndInheritsFallback()
    {
        var result = ThemeJson.Parse("{}", HarborTheme.HarborWarm);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme.Name).IsEqualTo("custom");
        await Assert.That(result.Theme.Accent).IsEqualTo(HarborTheme.HarborWarm.Accent);
        await Assert.That(result.Theme.Background).IsEqualTo(HarborTheme.HarborWarm.Background);
    }

    [Test]
    public async Task Parse_TrailingCommas_Accepted()
    {
        var result = ThemeJson.Parse("""{ "accent": "#123456", }""", HarborTheme.HarborDark);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme.Accent).IsEqualTo(new RgbColor(0x12, 0x34, 0x56));
    }

    [Test]
    public async Task Parse_JsonComments_Accepted()
    {
        string json = """
            {
              // my favorite accent
              "name": "commented",
              "accent": "#123456", // inline note
            }
            """;

        var result = ThemeJson.Parse(json, HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme.Name).IsEqualTo("commented");
        await Assert.That(result.Theme.Accent).IsEqualTo(new RgbColor(0x12, 0x34, 0x56));
    }

    [Test]
    public async Task Parse_UnknownProperty_WarnsButLoads()
    {
        var result = ThemeJson.Parse("""{ "accent": "#123456", "vibes": "immaculate" }""", HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Warnings).Any(w => w.Contains("unknown property 'vibes'"));
    }

    [Test]
    public async Task Parse_PascalCaseKeys_AcceptedCaseInsensitively()
    {
        var result = ThemeJson.Parse("""{ "Accent": "#123456" }""", HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme.Accent).IsEqualTo(new RgbColor(0x12, 0x34, 0x56));
        await Assert.That(result.Warnings.Where(w => w.StartsWith("unknown", StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task Parse_LowContrast_LintsWarning()
    {
        // text nearly identical to background → well below WCAG AA 4.5:1
        string json = """{ "name": "camo", "text": "#101418", "background": "#14181d" }""";

        var result = ThemeJson.Parse(json, HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Warnings).Any(w => w.Contains("contrast") && w.Contains("text/background"));
    }

    [Test]
    public async Task Write_Parse_RoundTripsTheme()
    {
        string json = ThemeJson.Write(HarborTheme.HarborWarm);

        var result = ThemeJson.Parse(json, HarborTheme.HarborDark);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Theme).IsEqualTo(HarborTheme.HarborWarm);
    }

    [Test]
    public async Task TryParseHex_AcceptsShortLongBareAndRejects()
    {
        await Assert.That(ThemeJson.TryParseHex("#39bae6", out var six)).IsTrue();
        await Assert.That(six).IsEqualTo(new RgbColor(0x39, 0xBA, 0xE6));

        await Assert.That(ThemeJson.TryParseHex("#F0A", out var three)).IsTrue();
        await Assert.That(three).IsEqualTo(new RgbColor(0xFF, 0x00, 0xAA));

        await Assert.That(ThemeJson.TryParseHex("1A1F2B", out var bare)).IsTrue();
        await Assert.That(bare).IsEqualTo(new RgbColor(0x1A, 0x1F, 0x2B));

        await Assert.That(ThemeJson.TryParseHex("#12345", out _)).IsFalse();
        await Assert.That(ThemeJson.TryParseHex("#zzzzzz", out _)).IsFalse();
        await Assert.That(ThemeJson.TryParseHex("", out _)).IsFalse();
    }

    [Test]
    public async Task Hex_FormatsUppercaseWithHash()
    {
        await Assert.That(ThemeJson.Hex(new RgbColor(0x39, 0xBA, 0xE6))).IsEqualTo("#39BAE6");
    }
}
