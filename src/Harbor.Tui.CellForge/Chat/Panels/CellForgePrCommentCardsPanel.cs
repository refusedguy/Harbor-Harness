using System.Collections.Immutable;
using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cards for PR review comments (#1488). Each card shows author,
///     <c>file:line</c>, the comment body and a code context that starts folded.
/// </summary>
/// <remarks>
///     <para>
///         This panel rides the EXISTING panel axis (<see cref="IPanelProvider" />
///         / <c>PanelRegistry</c>) and the existing markdown/code pipeline. It
///         adds no new extension axis: no new widget primitive, no host route,
///         no registry of its own. Cards are drawn from the immutable input
///         contract <see cref="PrCommentCard" /> already loaded into
///         <see cref="TerminalUiState.PrComments" />; <c>Build</c> does no I/O.
///     </para>
///     <para>
///         Code context is tokenized with <see cref="CodeSyntaxTokenizer" />,
///         the same segmenter that highlights fenced code in
///         <c>AssistantMarkdownBlock</c>. CellForge panel rows are plain text,
///         so the tokens are concatenated back into display lines here; the
///         tokenizer is the single place that decides what a code line is.
///     </para>
///     <para>
///         Cursor lives in <c>UiState.Ui.PanelCursors</c> (TEA, #360); the
///         provider-local fallback covers only the null-store degraded path.
///         Expansion is view-local instance state, like
///         <c>AssistantMarkdownBlock.IsExpanded</c>, and render-thread only.
///     </para>
/// </remarks>
public sealed class CellForgePrCommentCardsPanel : CellForgePanelBase
{
    private readonly object _gate = new();
    private readonly CodeSyntaxTokenizer _tokenizer = new();
    private int _fallbackCursor;
    private bool _expanded;

    /// <inheritdoc />
    public override string Id => "pr-comment-cards";

    /// <inheritdoc />
    public override string Title => "PR Comments";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public override int DefaultSize => 44;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(ctx);
        var cards = ctx.State.Ui.PrCommentsFor(Id);
        int cursor = ResolveCursor(ctx, cards.Length);
        bool expanded;
        lock (_gate)
        {
            expanded = _expanded;
        }

        return PanelText.Clip(
            Rows(cards, cursor, expanded, _tokenizer, ctx.Width),
            ctx.Width,
            ctx.Height);
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(ctx);
        var cards = ctx.State.Ui.PrCommentsFor(Id);

        if (key.Code == UiKeyCode.Enter
            || (key.Code == UiKeyCode.Char && (key.Character == '\r' || key.Character == '\n')))
        {
            // Same host route as the file tree's Enter on a file: the host reads
            // the focused card's file:line from state and opens it with `read`.
            if (cards.Length > 0 && ctx.Deps.Store is { } store)
            {
                _ = store.Dispatch(new AppMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
            }

            return true;
        }

        if (key.Code != UiKeyCode.Char || key.Character is null || key.Mods.IsCommandModifier())
        {
            return false;
        }

        switch (key.Character)
        {
            case 'j':
            case 'J':
            {
                int next;
                lock (_gate)
                {
                    next = cards.Length == 0
                        ? 0
                        : Math.Min(cards.Length - 1, ResolveCursor(ctx, cards.Length) + 1);
                    _fallbackCursor = next;
                }

                DispatchCursor(ctx, next);
                return true;
            }

            case 'k':
            case 'K':
            {
                int next;
                lock (_gate)
                {
                    next = Math.Max(0, ResolveCursor(ctx, cards.Length) - 1);
                    _fallbackCursor = next;
                }

                DispatchCursor(ctx, next);
                return true;
            }

            case ' ':
                lock (_gate)
                {
                    _expanded = !_expanded;
                }

                return true;

            default:
                return false;
        }
    }

    private static void DispatchCursor(PanelContext ctx, int next)
    {
        if (ctx.Deps.Store is { } store)
        {
            _ = store.Dispatch(new AppMsg.SetPanelCursor("pr-comment-cards", next));
        }
    }

    private int ResolveCursor(PanelContext ctx, int count)
    {
        if (ctx.State.Ui.PanelCursors.TryGetValue(Id, out int stored))
        {
            return Math.Clamp(stored, 0, Math.Max(0, count - 1));
        }

        lock (_gate)
        {
            return Math.Clamp(_fallbackCursor, 0, Math.Max(0, count - 1));
        }
    }

    /// <summary>
    ///     Pure projection of the loaded cards into clipped cell rows. No state
    ///     mutation and no I/O; the golden frame is just this function's output.
    /// </summary>
    internal static List<string> Rows(
        ImmutableArray<PrCommentCard> cards,
        int cursor,
        bool expanded,
        CodeSyntaxTokenizer tokenizer,
        int width)
    {
        int contentWidth = Math.Max(1, width - 4);
        var rows = new List<string>(4 + cards.Length * 6)
        {
            $"PR Comments ({cards.Length})",
            PanelText.Separator,
        };

        if (cards.Length == 0)
        {
            rows.Add("No PR comments loaded.");
            rows.Add("Open a review to see comment cards.");
            return rows;
        }

        for (int i = 0; i < cards.Length; i++)
        {
            PrCommentCard card = cards[i];
            string marker = i == cursor ? ">" : " ";
            rows.Add($"{marker} {Clip($"{card.Author}  {card.FilePath}:{card.Line}", contentWidth)}");

            string[] bodyLines = (card.Body ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (bodyLines.Length == 0)
            {
                bodyLines = ["(no comment body)"];
            }

            for (int b = 0; b < bodyLines.Length; b++)
            {
                rows.Add(Clip("  " + bodyLines[b], contentWidth));
            }

            bool showContext = expanded && i == cursor;
            rows.Add(showContext
                ? $"  ▾ code ({card.CodeContext.Length} lines)"
                : $"  ▸ code ({card.CodeContext.Length} lines)  [space]");

            if (!showContext)
            {
                continue;
            }

            var text = new StringBuilder();
            bool inBlockComment = false;
            for (int c = 0; c < card.CodeContext.Length; c++)
            {
                text.Clear().Append("  │ ");
                var spans = tokenizer.TokenizeLine(card.CodeContext[c].AsSpan(), card.Language, ref inBlockComment);
                for (int s = 0; s < spans.Count; s++)
                {
                    text.Append(spans[s].Text);
                }

                rows.Add(Clip(text.ToString(), contentWidth));
            }
        }

        return rows;
    }

    private static string Clip(string line, int width)
        => line.Length <= width ? line : line[..width] + "…";
}
