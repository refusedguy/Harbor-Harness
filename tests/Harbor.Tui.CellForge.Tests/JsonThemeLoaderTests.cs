using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Custom JSON themes: hex parsing (#RGB/#RRGGBB), full and partial catalogs
/// (omitted slots merge over the active theme), error reporting, and file
/// loading.
/// </summary>
public class JsonThemeLoaderTests
{
    [Test]
    public async Task TryParseHex_AcceptsShortLongAndRejects()
    {
        await Assert.That(JsonThemeLoader.TryParseHex("#39bae6", out var six)).IsTrue();
        await Assert.That(six).IsEqualTo(new RgbColor(0x39, 0xBA, 0xE6));

        await Assert.That(JsonThemeLoader.TryParseHex("#F0A", out var three)).IsTrue();
        await Assert.That(three).IsEqualTo(new RgbColor(0xFF, 0x00, 0xAA));

        await Assert.That(JsonThemeLoader.TryParseHex("1A1F2B", out var bare)).IsTrue();
        await Assert.That(bare).IsEqualTo(new RgbColor(0x1A, 0x1F, 0x2B));

        await Assert.That(JsonThemeLoader.TryParseHex("#12345", out _)).IsFalse();
        await Assert.That(JsonThemeLoader.TryParseHex("#zzzzzz", out _)).IsFalse();
        await Assert.That(JsonThemeLoader.TryParseHex("", out _)).IsFalse();
    }

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

        var result = JsonThemeLoader.Parse(json);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsEqualTo("sunset");
        await Assert.That(result.Value.Accent).IsEqualTo(new RgbColor(0xFF, 0x88, 0x00));
        await Assert.That(result.Value.Text).IsEqualTo(new RgbColor(0xFF, 0xFF, 0xFF));
        await Assert.That(result.Value.Background).IsEqualTo(new RgbColor(0x10, 0x08, 0x0A));
    }

    [Test]
    public async Task Parse_Partial_MergesOverActiveTheme()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborCool);
        try
        {
            var result = JsonThemeLoader.Parse("""{ "name": "cool-plus", "accent": "#123456" }""");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Accent).IsEqualTo(new RgbColor(0x12, 0x34, 0x56));
            await Assert.That(result.Value.Text).IsEqualTo(HarborTheme.HarborCool.Text);       // merged
            await Assert.That(result.Value.Background).IsEqualTo(HarborTheme.HarborCool.Background);
        }
        finally
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
        }
    }

    [Test]
    public async Task Parse_InvalidSlot_FailsWithSlotName()
    {
        var result = JsonThemeLoader.Parse("""{ "accent": "#nope" }""");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("accent");
    }

    [Test]
    public async Task Parse_MalformedJson_Fails()
    {
        var result = JsonThemeLoader.Parse("{ not json ");

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Parse_DefaultsName_WhenMissing()
    {
        var result = JsonThemeLoader.Parse("{}");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsEqualTo("custom");
    }

    [Test]
    public async Task LoadFile_MissingFile_FailsCleanly()
    {
        var result = JsonThemeLoader.LoadFile("/nonexistent/harbor-theme.json");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not found");
    }

    [Test]
    public async Task LoadFile_ReadsDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-theme-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "name": "disk", "accent": "#abcdef" }""");
            var result = JsonThemeLoader.LoadFile(path);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Name).IsEqualTo("disk");
            await Assert.That(result.Value.Accent).IsEqualTo(new RgbColor(0xAB, 0xCD, 0xEF));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
