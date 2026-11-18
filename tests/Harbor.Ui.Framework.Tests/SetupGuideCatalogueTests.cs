using Harbor.Ui.Framework.Strings;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Issue #434, one-component extraction slice: <c>SetupChecklistOverlay</c>'s modal
/// header and dismiss hint are keys in this catalogue instead of literals in the
/// widget. These tests pin both shipped locales and the pinned-locale seam a
/// consumer uses, without touching <c>HARBOR_LOCALE</c> or ambient culture.
/// </summary>
public sealed class SetupGuideCatalogueTests
{
    private const string TitleKey = "SetupGuide_Title";
    private const string FooterKey = "SetupGuide_FooterHint";
    private const string EnTitle = "Setup guide";
    private const string EnFooter = "Esc to close · /setup to reopen";
    private const string RuTitle = "Руководство по настройке";
    private const string RuFooter = "Esc — закрыть · /setup — открыть снова";

    [Test]
    public async Task EnLocale_RendersSourceOfTruth()
    {
        await Assert.That(UiStrings.Get(TitleKey, "en")).IsEqualTo(EnTitle);
        await Assert.That(UiStrings.Get(FooterKey, "en")).IsEqualTo(EnFooter);
    }

    [Test]
    public async Task RuLocale_RendersTranslation()
    {
        await Assert.That(UiStrings.Get(TitleKey, "ru")).IsEqualTo(RuTitle);
        await Assert.That(UiStrings.Get(FooterKey, "ru")).IsEqualTo(RuFooter);
        await Assert.That(UiStrings.Get(TitleKey, "ru")).IsNotEqualTo(UiStrings.Get(TitleKey, "en"));
        await Assert.That(UiStrings.Get(FooterKey, "ru")).IsNotEqualTo(UiStrings.Get(FooterKey, "en"));
    }

    [Test]
    public async Task Keys_AreAudited()
    {
        await Assert.That(UiStrings.Keys.Contains(TitleKey)).IsTrue();
        await Assert.That(UiStrings.Keys.Contains(FooterKey)).IsTrue();
        await Assert.That(new UiStringsCatalog().GetEnglishEntries().ContainsKey(TitleKey)).IsTrue();
        await Assert.That(new UiStringsCatalog().GetEnglishEntries().ContainsKey(FooterKey)).IsTrue();
    }

    /// <summary>The injected, pinned-locale catalogue the overlay uses in tests.</summary>
    [Test]
    public async Task PinnedLocale_ResolvesWithoutProcessState()
    {
        await Assert.That(new UiStringsCatalog("ru").Get(TitleKey)).IsEqualTo(RuTitle);
        await Assert.That(new UiStringsCatalog("ru").Get(FooterKey)).IsEqualTo(RuFooter);
        await Assert.That(new UiStringsCatalog("en").Get(TitleKey)).IsEqualTo(EnTitle);
        await Assert.That(new UiStringsCatalog("en").Get(FooterKey)).IsEqualTo(EnFooter);
    }
}
