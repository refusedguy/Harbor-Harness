namespace Harbor.Ui.Framework.State;

/// <summary>
///     Screenshot-markup overlay transitions of the chat reducer: pure folds
///     over <c>Chat.Markup</c> that no-op (return the input state) when the
///     overlay is closed — keys never reach the agent while it is open because
///     the host stops at the markup barrier first (<c>ReplInputLoop</c> checks
///     <c>Markup.IsOpen</c> before the palette), not because the reducer
///     double-guesses the host. Extracted verbatim from
///     <see cref="ChatAppReducer" /> (#95). The host runs the effects; nothing
///     here touches I/O or DI.
/// </summary>
/// <remarks>
///     <see cref="ChatAppReducer" /> keeps a thin forwarder over every entry
///     point, so existing call sites, tests and doc references keep resolving
///     to the same signatures with the same behaviour.
/// </remarks>
public static class MarkupReducer
{
    internal static ReduceResult WithMarkup(UiState state, Func<MarkupOverlayState, MarkupOverlayState> fold)
    {
        var markup = state.Chat.Markup;
        if (!markup.IsOpen)
        {
            return ReduceResult.NoOp(state);
        }

        var next = fold(markup);
        return ReferenceEquals(next, markup)
            ? ReduceResult.NoOp(state)
            : ReduceResult.NoOp(state with { Chat = state.Chat with { Markup = next } });
    }

    internal static MarkupOverlayState PlaceAtCursor(MarkupOverlayState markup)
    {
        MarkupAnnotationModel model = markup.ActiveTool switch
        {
            MarkupKind.Arrow => markup.Model.Add(
                MarkupKind.Arrow, markup.Cursor, markup.Cursor.Shift(0.15, 0.1)),
            MarkupKind.Rectangle => markup.Model.Add(
                MarkupKind.Rectangle, markup.Cursor.Shift(-0.075, -0.05), markup.Cursor.Shift(0.075, 0.05)),
            _ => string.IsNullOrEmpty(markup.PendingText)
                ? markup.Model
                : markup.Model.Add(MarkupKind.Text, markup.Cursor, markup.Cursor, text: markup.PendingText),
        };

        if (ReferenceEquals(model, markup.Model))
        {
            return markup;
        }

        var next = markup with { Model = model, Error = string.Empty };
        return markup.ActiveTool == MarkupKind.Text ? next with { PendingText = string.Empty } : next;
    }

    internal static MarkupOverlayState SelectAtPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        int? hit = MarkupOverlayState.HitTest(markup.Model, point);
        return hit is { } id
            ? markup with { Model = markup.Model.Select(id) }
            : markup with { Cursor = point };
    }

    internal static MarkupOverlayState PressAtPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        int? hit = MarkupOverlayState.HitTest(markup.Model, point);
        if (hit is { } id)
        {
            return markup with
            {
                Model = markup.Model.Select(id).Checkpoint(),
                Draft = new MarkupDraft(point, point, Moving: true),
            };
        }

        return markup with { Draft = new MarkupDraft(point, point, Moving: false), Cursor = point };
    }

    internal static MarkupOverlayState DragToPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        if (markup.Draft is not { } draft)
        {
            return markup;
        }

        if (draft.Moving)
        {
            var moved = markup.Model.NudgeSelected(point.X - draft.Current.X, point.Y - draft.Current.Y);
            return markup with { Model = moved, Draft = draft with { Current = point } };
        }

        return markup with { Draft = draft with { Current = point } };
    }

    internal static MarkupOverlayState ReleaseAtPoint(MarkupOverlayState markup, NormalizedPoint point)
    {
        if (markup.Draft is not { } draft)
        {
            return markup;
        }

        if (draft.Moving)
        {
            // The press recorded the checkpoint; the drags were transient, so
            // the whole gesture undoes in one step. A press+release without
            // motion still spent a frame — collapse it so empty drags leave
            // no undo trace.
            var model = draft.Current.Equals(draft.Anchor)
                ? markup.Model.UndoFrame()
                : markup.Model.NudgeSelected(point.X - draft.Current.X, point.Y - draft.Current.Y);
            return markup with { Model = model, Draft = null };
        }

        MarkupAnnotationModel model2 = markup.ActiveTool switch
        {
            MarkupKind.Text when string.IsNullOrEmpty(markup.PendingText) => markup.Model,
            MarkupKind.Text => markup.Model.Add(MarkupKind.Text, draft.Anchor, draft.Anchor, text: markup.PendingText),
            _ => markup.Model.Add(markup.ActiveTool, draft.Anchor, point),
        };

        if (ReferenceEquals(model2, markup.Model))
        {
            return markup with { Draft = null };
        }

        var next = markup with { Model = model2, Draft = null, Cursor = point, Error = string.Empty };
        return markup.ActiveTool == MarkupKind.Text ? next with { PendingText = string.Empty } : next;
    }
}
