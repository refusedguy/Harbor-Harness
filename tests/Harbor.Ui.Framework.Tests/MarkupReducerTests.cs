using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Reducer acceptance for issue #400 slice 1/2: the markup overlay opens
///     on an image row, edits fold through the store, and Esc closes it —
///     with the scroll snapshot intact for the host to restore.
/// </summary>
public class MarkupReducerTests
{
    private static UiState Open() =>
        ChatAppReducer.Update(new UiState(), new ChatAppMsg.OpenMarkup(
            "/shots/broken.png", "broken.png", 800, 600, ScrollOffset: 12)).State;

    [Test]
    public async Task Open_SnapshotsScroll_AndStartsEmpty()
    {
        var state = Open();
        var markup = state.Chat.Markup;

        await Assert.That(markup.IsOpen).IsTrue();
        await Assert.That(markup.SourceName).IsEqualTo("broken.png");
        await Assert.That(markup.SavedScrollOffset).IsEqualTo(12);
        await Assert.That(markup.Items.Length).IsEqualTo(0);
        await Assert.That(markup.Cursor).IsEqualTo(NormalizedPoint.Create(0.5, 0.5));
    }

    [Test]
    public async Task Close_DiscardsSession()
    {
        var state = ChatAppReducer.Update(Open(), new ChatAppMsg.CloseMarkup()).State;

        await Assert.That(state.Chat.Markup.IsOpen).IsFalse();
        await Assert.That(state.Chat.Markup).IsEqualTo(MarkupOverlayState.Closed);
    }

    [Test]
    public async Task Place_AddsActiveTool_AtCursor()
    {
        var state = ChatAppReducer.Update(Open(), new ChatAppMsg.MarkupPlace()).State;

        await Assert.That(state.Chat.Markup.Items.Length).IsEqualTo(1);
        await Assert.That(state.Chat.Markup.Items[0].Kind).IsEqualTo(MarkupKind.Arrow);
        await Assert.That(state.Chat.Markup.SelectedId).IsEqualTo(1);
    }

    [Test]
    public async Task TextPlace_NeedsPendingText_AndClearsIt()
    {
        var tooEarly = ChatAppReducer.Update(Open(), new ChatAppMsg.MarkupSelectTool(MarkupKind.Text)).State;
        tooEarly = ChatAppReducer.Update(tooEarly, new ChatAppMsg.MarkupPlace()).State;
        await Assert.That(tooEarly.Chat.Markup.Items.Length).IsEqualTo(0);

        var typed = ChatAppReducer.Update(tooEarly, new ChatAppMsg.MarkupSetPendingText("here")).State;
        var placed = ChatAppReducer.Update(typed, new ChatAppMsg.MarkupPlace()).State;
        await Assert.That(placed.Chat.Markup.Items.Length).IsEqualTo(1);
        await Assert.That(placed.Chat.Markup.Items[0].Text).IsEqualTo("here");
        await Assert.That(placed.Chat.Markup.PendingText).IsEmpty();
    }

    [Test]
    public async Task Nudge_Undo_Redo_RoundTrip()
    {
        var state = Open();
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupPlace()).State;
        double before = state.Chat.Markup.Items[0].From.X;

        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupNudge(0.1, 0)).State;
        await Assert.That(state.Chat.Markup.Items[0].From.X).IsEqualTo(before + 0.1);

        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupUndo()).State;
        await Assert.That(state.Chat.Markup.Items[0].From.X).IsEqualTo(before);

        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupRedo()).State;
        await Assert.That(state.Chat.Markup.Items[0].From.X).IsEqualTo(before + 0.1);
    }

    [Test]
    public async Task MouseDrag_PlacesRectangle_ThroughDraft()
    {
        var state = Open();
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupSelectTool(MarkupKind.Rectangle)).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupPressAt(0.2, 0.2)).State;
        await Assert.That(state.Chat.Markup.Draft is not null).IsTrue();

        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupDragTo(0.4, 0.5)).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupReleaseAt(0.4, 0.5)).State;

        var markup = state.Chat.Markup;
        await Assert.That(markup.Draft is null).IsTrue();
        await Assert.That(markup.Items.Length).IsEqualTo(1);
        await Assert.That(markup.Items[0].From).IsEqualTo(NormalizedPoint.Create(0.2, 0.2));
        await Assert.That(markup.Items[0].To).IsEqualTo(NormalizedPoint.Create(0.4, 0.5));
    }

    [Test]
    public async Task Click_SelectsHit_OrMovesCursor()
    {
        var state = Open();
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupPlace()).State;
        var first = state.Chat.Markup.Items[0];
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupSelectTool(MarkupKind.Rectangle)).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupPlace()).State;
        await Assert.That(state.Chat.Markup.SelectedId).IsEqualTo(2);

        // Click on the first arrow selects it…
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupSelectAt(first.From.X, first.From.Y)).State;
        await Assert.That(state.Chat.Markup.SelectedId).IsEqualTo(first.Id);

        // …click on empty space moves the cursor instead.
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupSelectAt(0.95, 0.95)).State;
        await Assert.That(state.Chat.Markup.Cursor).IsEqualTo(NormalizedPoint.Create(0.95, 0.95));
        await Assert.That(state.Chat.Markup.SelectedId).IsEqualTo(first.Id);
    }

    [Test]
    public async Task Failed_SetsInlineError_Saved_ClearsIt()
    {
        var state = Open();
        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupFailed("unreadable PNG")).State;
        await Assert.That(state.Chat.Markup.Error).IsEqualTo("unreadable PNG");

        state = ChatAppReducer.Update(state, new ChatAppMsg.MarkupSaved("/shots/broken.annotated.png")).State;
        await Assert.That(state.Chat.Markup.Error).IsEmpty();
        await Assert.That(state.Chat.Markup.SavedPath).IsEqualTo("/shots/broken.annotated.png");
    }

    [Test]
    public async Task Arms_NoOp_WhenClosed()
    {
        var closed = new UiState();
        var result = ChatAppReducer.Update(closed, new ChatAppMsg.MarkupPlace());

        await Assert.That(result.State).IsSameReferenceAs(closed);
    }
}
