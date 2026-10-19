using Harbor.Ui.Framework.Strings;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Issue #1573: the string catalogue is a DI singleton (<see cref="IUiStrings" />
/// / <see cref="UiStringsCatalog" />), not the static process-global
/// <c>UiStrings</c>. These tests pin the instance contract the issue calls for:
/// a catalogue with a pinned locale resolves fully without <c>HARBOR_LOCALE</c>
/// mutation or ambient-culture dependence (the substitution seam), and two
/// instances resolve independently — proof the global state is gone.
/// </summary>
public class UiStringsServiceTests
{
    /// <summary>The parameterless service and remaining static forwarders agree:
    ///     the shared default instance is the same catalogue underneath.
    /// </summary>
    [Test]
    public async Task ParameterlessInstance_MatchesStaticForwarder()
    {
        var service = new UiStringsCatalog();
        await Assert.That(service.CurrentLocale).IsEqualTo(UiStrings.CurrentLocale);
        await Assert.That(service.Get("Onboarding_Welcome")).IsEqualTo(UiStrings.Get("Onboarding_Welcome"));
        await Assert.That(service.Get("Onboarding_Welcome", "en"))
            .IsEqualTo(UiStrings.Get("Onboarding_Welcome", "en"));

        IReadOnlyDictionary<string, string> entries = service.GetEnglishEntries();
        await Assert.That(entries.Count).IsEqualTo(UiStrings.Keys.Count);
    }

    /// <summary>
    /// A pinned instance resolves the translated satellite through
    /// <see cref="IUiStrings.Get(string)" /> alone — no setenv step anywhere.
    /// </summary>
    [Test]
    public async Task PinnedLocale_GetWithoutLocale_UsesPinnedLocale()
    {
        var ru = new UiStringsCatalog("ru");
        await Assert.That(ru.CurrentLocale).IsEqualTo("ru");
        await Assert.That(ru.Get("Onboarding_Welcome")).IsEqualTo("Добро пожаловать в Harbor");
        await Assert.That(ru.Get("Settings_Title")).IsEqualTo("Настройки");
    }

    /// <summary>
    /// Pinning does not bypass the fallback contract a key missing from the
    /// pinned satellite still renders its <c>en</c> text.
    /// </summary>
    [Test]
    public async Task PinnedLocale_MissingKey_FallsBackToEn()
    {
        var ru = new UiStringsCatalog("ru");
        await Assert.That(ru.Get("Palette_Prompt")).IsEqualTo("Type a command…");
        await Assert.That(ru.Get("Toast_SaveFailed")).IsEqualTo("Save failed");
    }

    /// <summary>Normalization applies to the pin too: unknown locale means <c>en</c>.</summary>
    [Test]
    public async Task PinnedLocale_UnknownValue_FallsBackToEn()
    {
        var de = new UiStringsCatalog("de");
        await Assert.That(de.CurrentLocale).IsEqualTo("en");
        await Assert.That(de.Get("Onboarding_Welcome")).IsEqualTo("Welcome to Harbor");

        var ruRu = new UiStringsCatalog("ru-RU");
        await Assert.That(ruRu.CurrentLocale).IsEqualTo("ru");
        await Assert.That(ruRu.Get("Cli_NoSessions")).IsEqualTo("Нет сессий.");
    }

    /// <summary>
    /// The exact defect the static shape had: ambient/global locale is shared by
    /// every caller, so two lookups under different "current" locales cannot
    /// coexist. Two service instances disagree on nothing — same key, pinned
    /// independently.
    /// </summary>
    [Test]
    public async Task TwoInstances_ResolveIndependently_NoSharedState()
    {
        var ru = new UiStringsCatalog("ru");
        var en = new UiStringsCatalog("en");
        await Assert.That(ru.Get("Settings_Title")).IsEqualTo("Настройки");
        await Assert.That(en.Get("Settings_Title")).IsEqualTo("Settings");
        await Assert.That(en.Get("Cli_NoSessions")).IsEqualTo("No sessions.");

        // The static forwarder still resolves en regardless of the instances above:
        await Assert.That(UiStrings.Get("Settings_Title", "en")).IsEqualTo("Settings");
    }

    /// <summary>
    /// The drift guard contract rides along the instance seam too: every
    /// <see cref="UiStrings.Keys" /> key resolves to rendered text on the
    /// service, and no neutral entry is outside the audited list.
    /// </summary>
    [Test]
    public async Task Instance_GetEnglishEntries_MatchesAuditedKeys()
    {
        var service = new UiStringsCatalog();
        IReadOnlyDictionary<string, string> entries = service.GetEnglishEntries();

        foreach (string key in UiStrings.Keys)
        {
            await Assert.That(entries.ContainsKey(key)).IsTrue();
            await Assert.That(service.Get(key, "en")).IsNotEqualTo(key);
        }

        foreach (string name in entries.Keys)
        {
            await Assert.That(UiStrings.Keys.Contains(name)).IsTrue();
        }
    }
}
