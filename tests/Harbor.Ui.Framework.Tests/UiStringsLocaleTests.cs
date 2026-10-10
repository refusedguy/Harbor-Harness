using System.Globalization;
using Harbor.Ui.Framework.Strings;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Issue #434, slice 2: the shared catalogue ships <c>en</c> (source of truth) plus
/// <c>ru</c>, and a key missing from the requested satellite renders <c>en</c> —
/// never a raw resource key. These tests pin the locale behaviour through the
/// explicit-locale overloads, so they stay hermetic (no env mutation, no
/// ambient-culture dependence); <c>HARBOR_LOCALE</c> resolution itself is covered
/// through the pure <see cref="UiStrings.ResolveLocale" />.
/// </summary>
public class UiStringsLocaleTests
{
    [Test]
    public async Task EnLocale_RendersSourceOfTruth()
    {
        await Assert.That(UiStrings.Get("Onboarding_Welcome", "en")).IsEqualTo("Welcome to Harbor");
        await Assert.That(UiStrings.Get("Palette_Prompt", "en")).IsEqualTo("Type a command…");
        await Assert.That(UiStrings.Get("Cli_NoSessions", "en")).IsEqualTo("No sessions.");
    }

    [Test]
    public async Task RuLocale_RendersTranslated()
    {
        await Assert.That(UiStrings.Get("Onboarding_Welcome", "ru")).IsEqualTo("Добро пожаловать в Harbor");
        await Assert.That(UiStrings.Get("Settings_Title", "ru")).IsEqualTo("Настройки");
        await Assert.That(UiStrings.Get("Cli_NoSessions", "ru")).IsEqualTo("Нет сессий.");
    }

    /// <summary>
    /// <c>Palette_Prompt</c> and <c>Toast_SaveFailed</c> are deliberately absent from
    /// the <c>ru</c> satellite: they must render their <c>en</c> text, not the key.
    /// </summary>
    [Test]
    public async Task MissingKeyInRu_FallsBackToEn()
    {
        await Assert.That(UiStrings.Get("Palette_Prompt", "ru")).IsEqualTo("Type a command…");
        await Assert.That(UiStrings.Get("Toast_SaveFailed", "ru")).IsEqualTo("Save failed");
    }

    [Test]
    public async Task UnknownLocale_FallsBackToEn()
    {
        await Assert.That(UiStrings.Get("Onboarding_Welcome", "de")).IsEqualTo("Welcome to Harbor");
        await Assert.That(UiStrings.Get("Onboarding_Welcome", "xx")).IsEqualTo("Welcome to Harbor");
        await Assert.That(UiStrings.Get("Onboarding_Welcome", string.Empty)).IsEqualTo("Welcome to Harbor");
    }

    [Test]
    public async Task UnknownKey_ReturnsKeyItself()
    {
        // Last resort only: no catalogue holds this key, so there is no en text to render.
        await Assert.That(UiStrings.Get("No_Such_Key", "ru")).IsEqualTo("No_Such_Key");
        await Assert.That(UiStrings.Get("No_Such_Key", "en")).IsEqualTo("No_Such_Key");
    }

    [Test]
    public async Task ResolveLocale_EnvWinsOverAmbient()
    {
        await Assert.That(UiStrings.ResolveLocale("ru", CultureInfo.GetCultureInfo("en"))).IsEqualTo("ru");
        await Assert.That(UiStrings.ResolveLocale("en", CultureInfo.GetCultureInfo("ru"))).IsEqualTo("en");
    }

    [Test]
    public async Task ResolveLocale_NormalizesRegionAndCase()
    {
        await Assert.That(UiStrings.ResolveLocale("ru-RU")).IsEqualTo("ru");
        await Assert.That(UiStrings.ResolveLocale("RU")).IsEqualTo("ru");
        await Assert.That(UiStrings.ResolveLocale("en-US")).IsEqualTo("en");
    }

    [Test]
    public async Task ResolveLocale_UnknownEnvMeansEn()
    {
        await Assert.That(UiStrings.ResolveLocale("de")).IsEqualTo("en");
        await Assert.That(UiStrings.ResolveLocale("  ")).IsEqualTo("en");
    }

    [Test]
    public async Task ResolveLocale_EmptyEnvDonatesAmbientLanguage()
    {
        await Assert.That(UiStrings.ResolveLocale(null, CultureInfo.GetCultureInfo("ru"))).IsEqualTo("ru");
        await Assert.That(UiStrings.ResolveLocale(null, CultureInfo.GetCultureInfo("ru-RU"))).IsEqualTo("ru");
        await Assert.That(UiStrings.ResolveLocale(null, CultureInfo.GetCultureInfo("de"))).IsEqualTo("en");
        await Assert.That(UiStrings.ResolveLocale(null, CultureInfo.InvariantCulture)).IsEqualTo("en");
    }

    /// <summary>
    /// <c>UiStrings.Keys</c> must match the neutral resources one-for-one: a key added
    /// to the <c>.resx</c> without a list entry (or vice versa) fails here instead of
    /// silently leaving a string un-auditable for the extraction slices.
    /// </summary>
    [Test]
    public async Task Keys_MatchEnglishCatalogue()
    {
        IReadOnlyDictionary<string, string> entries = UiStrings.GetEnglishEntries();
        await Assert.That(entries.Count).IsEqualTo(UiStrings.Keys.Count);

        foreach (string key in UiStrings.Keys)
        {
            await Assert.That(entries.ContainsKey(key)).IsTrue();
            await Assert.That(UiStrings.Get(key, "en")).IsNotEqualTo(key);
        }

        foreach (string name in entries.Keys)
        {
            await Assert.That(UiStrings.Keys.Contains(name)).IsTrue();
        }
    }
}
