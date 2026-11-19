// SettingsSearchTests.cs — K29: the Settings search filter.
//
// SettingsView filters SettingsSections.All through SettingsSearch.IsMatch
// before handing the list to the Accordion, and the view-model computes the
// no-results flag through SettingsSearch.HasMatches. Both call the same pure
// methods, so this file pins the matching behaviour that keeps the two in
// step. No Avalonia app is stood up: there is no window, no config store and
// no dispatcher in the code under test.

using Harbor.App.Avalonia.ViewModels;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

public class SettingsSearchTests
{
    [Test]
    public async Task Empty_OrWhitespaceQuery_ShowsEverySection()
    {
        foreach (string section in SettingsSearch.Sections)
        {
            await Assert.That(SettingsSearch.IsMatch(section, "")).IsTrue();
            await Assert.That(SettingsSearch.IsMatch(section, "   ")).IsTrue();
        }
        await Assert.That(SettingsSearch.HasMatches("")).IsTrue();
        await Assert.That(SettingsSearch.HasMatches("   ")).IsTrue();
    }

    [Test]
    public async Task Query_MatchesAnyAlias_IgnoringCase()
    {
        await Assert.That(SettingsSearch.IsMatch(SettingsSearch.ThemeAliases, "palette")).IsTrue();
        await Assert.That(SettingsSearch.IsMatch(SettingsSearch.ThemeAliases, "PALETTE")).IsTrue();
        await Assert.That(SettingsSearch.IsMatch(SettingsSearch.ThemeAliases, "appearance")).IsTrue();
        await Assert.That(SettingsSearch.IsMatch(SettingsSearch.ProviderConfigurationAliases, "api key")).IsTrue();
        await Assert.That(SettingsSearch.IsMatch(SettingsSearch.OllamaHostAliases, "ollama")).IsTrue();
    }

    [Test]
    public async Task Query_ThatMatchesNoSection_ProducesTheEmptyState()
    {
        await Assert.That(SettingsSearch.HasMatches("no-such-setting-xyz")).IsFalse();
        await Assert.That(SettingsSearch.IsMatch(SettingsSearch.ThemeAliases, "storage")).IsFalse();
        await Assert.That(SettingsSearch.IsMatch(null, "theme")).IsFalse();
    }

    [Test]
    public async Task SectionAliases_AreAllDistinctAndNonEmpty()
    {
        await Assert.That(SettingsSearch.Sections.Count).IsEqualTo(10);
        await Assert.That(SettingsSearch.Sections.Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(SettingsSearch.Sections.Count);
        foreach (string section in SettingsSearch.Sections)
        {
            await Assert.That(section).IsNotEmpty();
            await Assert.That(section.Contains('|')).IsTrue();
        }
    }
}
