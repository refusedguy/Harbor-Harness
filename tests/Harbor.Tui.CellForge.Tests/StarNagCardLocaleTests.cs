// StarNagCardLocaleTests.cs — issue #434, one-component extraction slice.
// The star-nag toast text no longer lives in the component: it is a key in the
// shared UiStrings catalogue (en source of truth + ru satellite). These tests
// pin both the catalogue entries and that the card renders the text for the
// locale it is given, without touching HARBOR_LOCALE or the ambient culture.

using Harbor.Tui.CellForge.StarNag;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Strings;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

public sealed class StarNagCardLocaleTests
{
    private const string EnMessage = "Star Harbor on GitHub? s/n";
    private const string RuMessage = "Оцените Harbor на GitHub? s/n";

    /// <summary>Both shipped locales carry the key; the ru value is a real translation.</summary>
    [Test]
    public async Task Catalogue_ShipsEnAndRuForStarNagMessage()
    {
        await Assert.That(UiStrings.Get(StarNagCard.MessageKey, "en")).IsEqualTo(EnMessage);
        await Assert.That(UiStrings.Get(StarNagCard.MessageKey, "ru")).IsEqualTo(RuMessage);
        await Assert.That(UiStrings.Get(StarNagCard.MessageKey, "ru"))
            .IsNotEqualTo(UiStrings.Get(StarNagCard.MessageKey, "en"));
    }

    /// <summary>
    /// The card's message is a catalogue lookup, not a literal: it agrees with
    /// the shared catalogue resolved under the current locale.
    /// </summary>
    [Test]
    public async Task Message_ResolvesThroughCatalogue()
    {
        var card = new StarNagCard();
        await Assert.That(card.Message).IsEqualTo(UiStrings.Get(StarNagCard.MessageKey));
        await Assert.That(card.Message).IsNotEqualTo(StarNagCard.MessageKey); // never a raw key
    }

    /// <summary>
    /// A pinned catalogue reaches the enqueued toast, so the locale choice is
    /// observable at the component's output and not just inside the resource layer.
    /// </summary>
    [Test]
    public async Task PinnedLocale_FlowsIntoToast()
    {
        var toasts = new ToastOverlay();
        var card = new StarNagCard(1, strings: new UiStringsCatalog("ru"));
        _ = card.TryShow(toasts, enabled: true);

        // One paint drains the pending queue into Active (same as the golden helper).
        var buffer = TestRender.Buffer(64, 14);
        toasts.Paint(buffer, toasts.ComputeBounds(new Rect(0, 0, 64, 14)));

        await Assert.That(toasts.Active.Count).IsEqualTo(1);
        await Assert.That(toasts.Active[0].Message).IsEqualTo(RuMessage);
    }
}
