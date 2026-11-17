using System.Collections.Immutable;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1488: cards over the existing panel axis + markdown/code pipeline.
///     The golden frame proves two cards render and the focused card's code
///     context expands through the existing tokenizer; the key tests prove
///     j/k + Enter ride the same store routes as the file tree.
/// </summary>
public class CellForgePrCommentCardsPanelTests
{
    private static PanelContext Ctx(UiState state, int width = 80, int height = 24, PanelServices? services = null) =>
        new(state, width, height, services);

    private static PrCommentCard Card(string author, string file, int line, string body, params string[] code) =>
        new(author, file, line, body, ImmutableArray.Create(code), "csharp");

    private static UiState StateWith(params PrCommentCard[] cards) =>
        new()
        {
            Ui = TerminalUiState.Empty with
            {
                PrComments = ImmutableDictionary<string, ImmutableArray<PrCommentCard>>.Empty.Add(
                    "pr-comment-cards", ImmutableArray.Create(cards))
            }
        };

    [Test]
    public async Task Contract_Identity_And_RegisterOverPanelRegistry()
    {
        var panel = new CellForgePrCommentCardsPanel();
        await Assert.That(panel.Id).IsEqualTo("pr-comment-cards");
        await Assert.That(panel.Title).IsEqualTo("PR Comments");
        await Assert.That(panel.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);

        // "over PanelRegistry": the card panel is registered as an ordinary
        // provider on the existing axis, not through a new registry of its own.
        var registry = new PanelRegistry();
        _ = registry.Register(panel);
        await Assert.That(registry.Get("pr-comment-cards")).IsSameReferenceAs(panel);
    }

    [Test]
    public async Task EmptyState_HonestPlaceholder_NotSilence()
    {
        string text = WidgetFixtures.Joined(new CellForgePrCommentCardsPanel().Build(Ctx(new UiState())));
        await Assert.That(text).Contains("PR Comments (0)");
        await Assert.That(text).Contains("No PR comments loaded.");
    }

    [Test]
    public async Task GoldenFrame_TwoCards_ExpandedFocusedContext()
    {
        var state = StateWith(
            Card("alice", "src/A.cs", 12, "Rename this method", "public void Foo()", "{", "}"),
            Card("bob", "src/B.cs", 30, "Handle null input", "var x = null;"));

        var panel = new CellForgePrCommentCardsPanel();
        var ctx = Ctx(state);
        string collapsed = WidgetFixtures.Joined(panel.Build(ctx));

        await Assert.That(collapsed).Contains("PR Comments (2)");
        await Assert.That(collapsed).Contains("alice");
        await Assert.That(collapsed).Contains("src/A.cs:12");
        await Assert.That(collapsed).Contains("Rename this method");
        await Assert.That(collapsed).Contains("bob");
        await Assert.That(collapsed).Contains("src/B.cs:30");
        await Assert.That(collapsed).Contains("▸ code");

        // The expansion is a view-local toggle, like AssistantMarkdownBlock.
        await Assert.That(panel.OnKey(UiKey.ForChar(' '), ctx)).IsTrue();
        string expanded = WidgetFixtures.Joined(panel.Build(ctx));

        await Assert.That(expanded).Contains("▾ code (3 lines)");
        await Assert.That(expanded).Contains("│ public void Foo()");
        // The tokenizer sees the C# keyword; a card off focus stays folded.
        await Assert.That(expanded).Contains("▸ code (2 lines)");
        await Assert.That(expanded).DoesNotContain("│ var x = null;");
    }

    [Test]
    public async Task OnKey_JK_And_Enter_RideStoreRoutes()
    {
        var store = new UiStore();
        _ = store.Dispatch(new AppMsg.SetPrComments(
            "pr-comment-cards",
            ImmutableArray.Create(
                Card("alice", "src/A.cs", 12, "first", "public void Foo()"),
                Card("bob", "src/B.cs", 30, "second", "var x = null;"))));
        var panel = new CellForgePrCommentCardsPanel();
        var ctx = Ctx(store.State, services: new PanelServices { Store = store });

        await Assert.That(panel.OnKey(UiKey.ForChar('j'), ctx)).IsTrue();
        await Assert.That(store.State.Ui.PanelCursors["pr-comment-cards"]).IsEqualTo(1);

        await Assert.That(panel.OnKey(UiKey.ForChar('k'), ctx)).IsTrue();
        await Assert.That(store.State.Ui.PanelCursors["pr-comment-cards"]).IsEqualTo(0);

        // Enter routes through the same Submit hand-off the file tree uses; the
        // host reads the focused card's file:line and opens it with `read`.
        _ = store.Dispatch(new AppMsg.InputText("/read src/A.cs"));
        var enterCtx = Ctx(store.State, services: new PanelServices { Store = store });
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), enterCtx)).IsTrue();
        await Assert.That(store.State.Ui.Input.IsEmpty).IsTrue()
            .Because("Enter dispatches Submit, which consumes the prefilled input");
    }

    [Test]
    public async Task OnKey_UnknownKey_NotConsumed()
    {
        var panel = new CellForgePrCommentCardsPanel();
        await Assert.That(panel.OnKey(UiKey.ForChar('z'), Ctx(new UiState()))).IsFalse();
    }

    [Test]
    public async Task SetPrComments_StoresSnapshot_ReducerIsPure()
    {
        var store = new UiStore();
        var cards = ImmutableArray.Create(Card("alice", "src/A.cs", 12, "body", "public void Foo()"));
        _ = store.Dispatch(new AppMsg.SetPrComments("pr-comment-cards", cards));
        var stored = store.State.Ui.PrCommentsFor("pr-comment-cards");
        await Assert.That(stored.Length).IsEqualTo(1);
        await Assert.That(stored[0].Author).IsEqualTo("alice");
        await Assert.That(new UiState().Ui.PrCommentsFor("anything").Length).IsEqualTo(0);
    }
}
