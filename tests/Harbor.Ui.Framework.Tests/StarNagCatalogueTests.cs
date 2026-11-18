using Harbor.Ui.Framework.Strings;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Issue #434, one-component extraction slice: <c>StarNagCard</c>'s toast text
/// is now a key in this catalogue instead of a literal in the component. These
/// tests pin both shipped locales and that an injected/pinned catalogue renders
/// the translation without touching <c>HARBOR_LOCALE</c> or ambient culture.
/// </summary>
public sealed class StarNagCatalogueTests
{
    private const string Key = "StarNag_Message";
    private const string EnMessage = "Star Harbor on GitHub? s/n";
    private const string RuMessage = "Оцените Harbor на GitHub? s/n";

    [Test]
    public async Task EnLocale_RendersSourceOfTruth()
    {
        await Assert.That(UiStrings.Get(Key, "en")).IsEqualTo(EnMessage);
    }

    [Test]
    public async Task RuLocale_RendersTranslation()
    {
        await Assert.That(UiStrings.Get(Key, "ru")).IsEqualTo(RuMessage);
        await Assert.That(UiStrings.Get(Key, "ru")).IsNotEqualTo(UiStrings.Get(Key, "en"));
    }

    [Test]
    public async Task Key_IsAudited()
    {
        await Assert.That(UiStrings.Keys.Contains(Key)).IsTrue();
        await Assert.That(new UiStringsCatalog().GetEnglishEntries().ContainsKey(Key)).IsTrue();
    }

    /// <summary>The pinned-locale seam a consumer such as StarNagCard uses.</summary>
    [Test]
    public async Task PinnedLocale_ResolvesWithoutProcessState()
    {
        await Assert.That(new UiStringsCatalog("ru").Get(Key)).IsEqualTo(RuMessage);
        await Assert.That(new UiStringsCatalog("en").Get(Key)).IsEqualTo(EnMessage);
    }
}
