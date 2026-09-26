using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-E-016: the CellForge theme default is the Harbor Terminal palette 1-to-1
/// with <c>apps/Harbor.App.Avalonia/Themes/Hds/HarborDesignTokens.axaml</c>.
/// Expected hexes are hardcoded from the axaml — any drift fails loudly.
/// Merge tests pin that JSON overrides win over <see cref="JsonThemeLoader.Default" />
/// while omitted slots fall back to it (explicit-fallback overload; the
/// single-arg <c>Parse</c> / <c>LoadFile</c> active-theme merge and the
/// <c>ThemeFileWatcher</c> live-reload path are untouched — see
/// <c>JsonThemeLoaderTests</c> / <c>ThemeFileWatcherTests</c>).
/// </summary>
public class ThemeDefaultTests
{
    [Test]
    public async Task Default_CatalogTokens_MatchAxamlHex()
    {
        var d = JsonThemeLoader.Default;

        await Assert.That(ThemeJson.Hex(d.Accent)).IsEqualTo("#39BAE6"); // AccentColor
        await Assert.That(ThemeJson.Hex(d.Success)).IsEqualTo("#7FD962"); // SuccessColor
        await Assert.That(ThemeJson.Hex(d.Warning)).IsEqualTo("#FFB454"); // WarningColor
        await Assert.That(ThemeJson.Hex(d.Error)).IsEqualTo("#FF6B6B"); // ErrorColor
        await Assert.That(ThemeJson.Hex(d.Tool)).IsEqualTo("#D2A6FF"); // MochaMauve
        await Assert.That(ThemeJson.Hex(d.System)).IsEqualTo("#F29668"); // MochaPeach
        await Assert.That(ThemeJson.Hex(d.User)).IsEqualTo("#39BAE6"); // ChatUserBrush
        await Assert.That(ThemeJson.Hex(d.Background)).IsEqualTo("#0A0E14"); // AppBackgroundBrush
        await Assert.That(ThemeJson.Hex(d.Panel)).IsEqualTo("#0D1117"); // PanelBackgroundBrush
        await Assert.That(ThemeJson.Hex(d.Surface)).IsEqualTo("#131820"); // CardBackgroundBrush
        await Assert.That(ThemeJson.Hex(d.Surface2)).IsEqualTo("#1A1F2B"); // CardElevatedBackgroundBrush
        await Assert.That(ThemeJson.Hex(d.Border)).IsEqualTo("#1F2430"); // BorderBrush
        await Assert.That(ThemeJson.Hex(d.Muted)).IsEqualTo("#5C6773"); // TextMutedBrush
        await Assert.That(ThemeJson.Hex(d.Text)).IsEqualTo("#B3B9C5"); // TextBrush
    }

    [Test]
    public async Task Default_ChatRoles_MatchAxamlHex()
    {
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatUser)).IsEqualTo("#39BAE6"); // ChatUserBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatAssistant)).IsEqualTo("#B3B9C5"); // ChatAssistantBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatThinking)).IsEqualTo("#5C6773"); // ChatThinkingBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatTool)).IsEqualTo("#D2A6FF"); // ChatToolBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatToolResult)).IsEqualTo("#7FD962"); // ChatToolResultBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatSystem)).IsEqualTo("#F29668"); // ChatSystemBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.ChatError)).IsEqualTo("#FF6B6B"); // ChatErrorBrush
    }

    [Test]
    public async Task Default_CostTokens_MatchAxamlHex()
    {
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.CostLow)).IsEqualTo("#7FD962"); // CostLowBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.CostMid)).IsEqualTo("#FFB454"); // CostMidBrush
        await Assert.That(ThemeJson.Hex(JsonThemeLoader.CostHigh)).IsEqualTo("#FF6B6B"); // CostHighBrush
    }

    [Test]
    public async Task Parse_EmptyJson_OverDefault_YieldsDefaultSlots()
    {
        var result = JsonThemeLoader.Parse("{}", JsonThemeLoader.Default);

        await Assert.That(result.IsSuccess).IsTrue();
        var d = JsonThemeLoader.Default;
        await Assert.That(result.Value.Accent).IsEqualTo(d.Accent);
        await Assert.That(result.Value.Success).IsEqualTo(d.Success);
        await Assert.That(result.Value.Warning).IsEqualTo(d.Warning);
        await Assert.That(result.Value.Error).IsEqualTo(d.Error);
        await Assert.That(result.Value.Tool).IsEqualTo(d.Tool);
        await Assert.That(result.Value.System).IsEqualTo(d.System);
        await Assert.That(result.Value.User).IsEqualTo(d.User);
        await Assert.That(result.Value.Background).IsEqualTo(d.Background);
        await Assert.That(result.Value.Panel).IsEqualTo(d.Panel);
        await Assert.That(result.Value.Surface).IsEqualTo(d.Surface);
        await Assert.That(result.Value.Surface2).IsEqualTo(d.Surface2);
        await Assert.That(result.Value.Border).IsEqualTo(d.Border);
        await Assert.That(result.Value.Muted).IsEqualTo(d.Muted);
        await Assert.That(result.Value.Text).IsEqualTo(d.Text);
    }

    [Test]
    public async Task Parse_PartialJson_OverDefault_OverridesOnlySpecified()
    {
        var result = JsonThemeLoader.Parse(
            """{ "name": "dimmed", "accent": "#123456", "background": "#000000" }""",
            JsonThemeLoader.Default);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsEqualTo("dimmed");
        await Assert.That(ThemeJson.Hex(result.Value.Accent)).IsEqualTo("#123456"); // overridden
        await Assert.That(ThemeJson.Hex(result.Value.Background)).IsEqualTo("#000000"); // overridden
        await Assert.That(result.Value.Text).IsEqualTo(JsonThemeLoader.Default.Text); // merged
        await Assert.That(result.Value.Border).IsEqualTo(JsonThemeLoader.Default.Border); // merged
        await Assert.That(result.Value.Success).IsEqualTo(JsonThemeLoader.Default.Success); // merged
    }

    [Test]
    public async Task Parse_InvalidHex_OverDefault_Fails()
    {
        var result = JsonThemeLoader.Parse("""{ "accent": "#nope" }""", JsonThemeLoader.Default);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("accent");
    }
}
