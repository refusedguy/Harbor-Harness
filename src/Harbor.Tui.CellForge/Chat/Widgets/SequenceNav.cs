// #2378: sequential navigation primitive (gpui-kit Pagination + Carousel, contract only).
// One primitive covers both: a sequence with an externally owned current index,
// a fold-to-next-state key handler, and one slot list the painter renders.
// Page and Carousel are configurations of that contract, not separate widgets.
// State lives OUTSIDE the widget in an immutable record struct; every edit
// returns a new state, so the host owns undo/render/serialization. BCL-only and
// AOT-safe like the other cell widgets; no extension axis (#555).

using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Which layout a <see cref="SequenceNav"/> renders for the same state.</summary>
public enum SequenceNavMode
{
    /// <summary>Pagination: first and last page pinned, ellipsis for the hidden run.</summary>
    Page,

    /// <summary>Carousel: a contiguous run of adjacent items around the selection.</summary>
    Carousel,
}

/// <summary>
/// Externally owned state of a sequential navigation control (#2378). Index is
/// always zero-based; <see cref="SequenceNav.Number" /> adds one for the
/// human-readable page/item label. Count is the number of positions. The host
/// stores the folded result of every key or selection request.
/// </summary>
/// <param name="Index">Current position, clamped for display by <see cref="SequenceNav.Normalize" />.</param>
/// <param name="Count">Number of positions; non-positive renders no slots.</param>
/// <param name="Mode">Page (ellipsis window) or Carousel (adjacent items).</param>
/// <param name="Loop">Wrap at the edges. Only Carousel honours it; paging clamps.</param>
/// <param name="Visible">Desired slot window width; Page floors it at five.</param>
/// <param name="Disabled">When set, no key or selection request is consumed.</param>
public readonly record struct SequenceNavState(
    int Index,
    int Count,
    SequenceNavMode Mode = SequenceNavMode.Page,
    bool Loop = false,
    int Visible = 5,
    bool Disabled = false);

/// <summary>One rendered destination: a 1-based number or an ellipsis gap.</summary>
/// <param name="Number">1-based page/item number; zero for a gap.</param>
/// <param name="IsGap">True for the ellipsis that stands for hidden positions.</param>
public readonly record struct SequenceNavSlot(int Number, bool IsGap)
{
    /// <summary>A visible destination.</summary>
    public static SequenceNavSlot Page(int number) => new(number, false);

    /// <summary>The gap between two visible runs.</summary>
    public static SequenceNavSlot Ellipsis => new(0, true);
}

/// <summary>
/// Pure state machine for sequential navigation (gpui-kit Pagination and
/// Carousel behaviors, #2378). Prev/next, Home/End, explicit selection and the
/// visible slot window all fold over <see cref="SequenceNavState" /> without
/// owning mutable data. Pagination keeps its 1-based label and ellipsis window;
/// Carousel keeps its 0-based selection and optional wrapping. Mouse drag,
/// GPUI code and event wiring are deliberately out of scope.
/// </summary>
public static class SequenceNav
{
    /// <summary>Left arrow of the control.</summary>
    public const char PreviousGlyph = '<';

    /// <summary>Right arrow of the control.</summary>
    public const char NextGlyph = '>';

    /// <summary>Glyph standing for hidden destinations.</summary>
    public const string GapGlyph = "…";

    /// <summary>Pagination configuration: clamped at both edges.</summary>
    public static SequenceNavState Page(
        int index,
        int count,
        int visible = 5,
        bool disabled = false) =>
        new(index, count, SequenceNavMode.Page, Loop: false, Visible: visible, Disabled: disabled);

    /// <summary>Carousel configuration: wrapping is opt-in.</summary>
    public static SequenceNavState Carousel(
        int index,
        int count,
        bool loop = false,
        int visible = 5,
        bool disabled = false) =>
        new(index, count, SequenceNavMode.Carousel, Loop: loop, Visible: visible, Disabled: disabled);

    /// <summary>Index clamped to [0, Count-1]; an empty sequence reads as zero.</summary>
    public static int Normalize(SequenceNavState state) =>
        state.Count <= 0 ? 0 : Math.Clamp(state.Index, 0, state.Count - 1);

    /// <summary>1-based label of the current position, used by both modes.</summary>
    public static int Number(SequenceNavState state) => Normalize(state) + 1;

    /// <summary>Whether Step and the arrow keys wrap. Paging always clamps.</summary>
    public static bool Wraps(SequenceNavState state) =>
        state.Loop && state.Mode == SequenceNavMode.Carousel && state.Count > 1;

    /// <summary>Whether a previous destination can be reached.</summary>
    public static bool HasPrevious(SequenceNavState state) =>
        !state.Disabled && state.Count > 1 && (Wraps(state) || Normalize(state) > 0);

    /// <summary>Whether a next destination can be reached.</summary>
    public static bool HasNext(SequenceNavState state) =>
        !state.Disabled
        && state.Count > 1
        && (Wraps(state) || Normalize(state) < state.Count - 1);

    /// <summary>Previous index, or null at the start edge when not wrapping.</summary>
    public static int? Previous(SequenceNavState state)
    {
        if (!HasPrevious(state))
        {
            return null;
        }

        int index = Normalize(state);
        return Wraps(state) && index == 0 ? state.Count - 1 : index - 1;
    }

    /// <summary>Next index, or null at the end edge when not wrapping.</summary>
    public static int? Next(SequenceNavState state)
    {
        if (!HasNext(state))
        {
            return null;
        }

        int index = Normalize(state);
        return Wraps(state) && index == state.Count - 1 ? 0 : index + 1;
    }

    /// <summary>First position of the sequence.</summary>
    public static int First(SequenceNavState state) => 0;

    /// <summary>Last position of the sequence; zero when it is empty.</summary>
    public static int Last(SequenceNavState state) => Math.Max(0, state.Count - 1);

    /// <summary>
    /// Moves <paramref name="delta" /> positions with wrapping or clamping,
    /// according to <see cref="Wraps" />. Disabled and empty states stay put.
    /// </summary>
    public static int Step(SequenceNavState state, int delta)
    {
        int index = Normalize(state);
        if (state.Disabled || state.Count <= 0 || delta == 0)
        {
            return index;
        }

        if (Wraps(state))
        {
            int n = state.Count;
            return ((index + delta) % n + n) % n;
        }

        return Math.Clamp(index + delta, 0, state.Count - 1);
    }

    /// <summary>
    /// Model-level selection request (gpui-kit <c>request_page</c>): rejects a
    /// disabled control, an empty sequence and an out-of-range destination.
    /// </summary>
    public static bool TrySelect(SequenceNavState state, int index, out SequenceNavState next)
    {
        next = default;
        if (state.Disabled || state.Count <= 0 || index < 0 || index >= state.Count)
        {
            return false;
        }

        next = state with { Index = index };
        return true;
    }

    /// <summary>
    /// Destinations visible for <paramref name="state" />. Both modes return the
    /// same slot type; only the layout differs. Page pins the first and last
    /// destinations and folds the hidden run into one ellipsis (gpui-kit's
    /// <c>calculate_items</c>). Carousel returns a contiguous window centered
    /// on the selection, which is its "related content" view.
    /// </summary>
    public static IReadOnlyList<SequenceNavSlot> Slots(SequenceNavState state)
    {
        int total = state.Count;
        if (total <= 0)
        {
            return Array.Empty<SequenceNavSlot>();
        }

        return state.Mode == SequenceNavMode.Carousel
            ? CarouselSlots(state, total)
            : PageSlots(state, total);
    }

    private static IReadOnlyList<SequenceNavSlot> PageSlots(SequenceNavState state, int total)
    {
        int current = Normalize(state) + 1;
        int maxVisible = Math.Max(5, state.Visible);
        if (total <= maxVisible)
        {
            var all = new SequenceNavSlot[total];
            for (int i = 0; i < total; i++)
            {
                all[i] = SequenceNavSlot.Page(i + 1);
            }

            return all;
        }

        var pages = new List<SequenceNavSlot>(maxVisible + 2) { SequenceNavSlot.Page(1) };
        int sidePages = (maxVisible - 3) / 2;
        int start = current <= sidePages + 1
            ? 2
            : current > total - sidePages - 1
                ? total - sidePages - 1
                : current - sidePages;

        if (start > 2)
        {
            pages.Add(SequenceNavSlot.Ellipsis);
        }

        int end = current >= total - sidePages
            ? total - 1
            : current <= sidePages + 1
                ? sidePages + 2
                : current + sidePages;

        for (int page = start; page <= end; page++)
        {
            pages.Add(SequenceNavSlot.Page(page));
        }

        if (end < total - 1)
        {
            pages.Add(SequenceNavSlot.Ellipsis);
        }

        pages.Add(SequenceNavSlot.Page(total));
        return pages;
    }

    private static IReadOnlyList<SequenceNavSlot> CarouselSlots(SequenceNavState state, int total)
    {
        int width = Math.Max(1, state.Visible);
        int index = Normalize(state);
        int start = Math.Clamp(index - (width / 2), 0, Math.Max(0, total - width));
        int end = Math.Min(total, start + width);
        var slots = new SequenceNavSlot[end - start];
        for (int i = start; i < end; i++)
        {
            slots[i - start] = SequenceNavSlot.Page(i + 1);
        }

        return slots;
    }

    /// <summary>
    /// Plain-text view: <c>&lt; 1 … 4 [5] 6 … 10 &gt;</c>, with square brackets
    /// marking the current destination. Stable enough for logs and goldens.
    /// </summary>
    public static string Text(SequenceNavState state)
    {
        var text = new StringBuilder();
        text.Append(PreviousGlyph).Append(' ');
        foreach (var slot in Slots(state))
        {
            if (slot.IsGap)
            {
                text.Append(GapGlyph);
            }
            else if (slot.Number == Number(state))
            {
                text.Append('[').Append(slot.Number).Append(']');
            }
            else
            {
                text.Append(slot.Number);
            }

            text.Append(' ');
        }

        return text.Append(NextGlyph).ToString();
    }

    /// <summary>
    /// Paints one row into <paramref name="rect" />: inactive arrows and gaps
    /// are dim, the current destination is bold. Degenerate rects are a no-op.
    /// </summary>
    public static void Paint(ScreenBuffer buffer, Rect rect, SequenceNavState state, CellStyle style = default)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var markup = new StringBuilder();
        markup.Append(HasPrevious(state)
            ? PreviousGlyph.ToString()
            : "[dim]" + PreviousGlyph + "[/]");

        int current = Number(state);
        var slots = Slots(state);
        foreach (var slot in slots)
        {
            markup.Append(' ');
            if (slot.IsGap)
            {
                markup.Append("[dim]").Append(GapGlyph).Append("[/]");
            }
            else if (slot.Number == current)
            {
                markup.Append("[bold][[").Append(slot.Number).Append("]][/]");
            }
            else
            {
                markup.Append(slot.Number);
            }
        }

        if (slots.Count > 0)
        {
            markup.Append(' ');
        }

        markup.Append(HasNext(state)
            ? NextGlyph.ToString()
            : "[dim]" + NextGlyph + "[/]");

        MarkupCells.Paint(buffer, rect.X, rect.Y, markup.ToString(), style);
    }

    /// <summary>
    /// Folds a key press into a new state. Left/Up/PageUp go to the previous
    /// destination, Right/Down/PageDown to the next, Home/End to the edges.
    /// Every other key and any host command chord is left to the outer router.
    /// </summary>
    /// <returns>True when the key was consumed.</returns>
    public static bool TryHandleKey(UiKey key, SequenceNavState state, out SequenceNavState next)
    {
        next = default;
        if (state.Disabled || key.Mods.IsCommandModifier())
        {
            return false;
        }

        int? target = key.Code switch
        {
            UiKeyCode.Left or UiKeyCode.Up or UiKeyCode.PageUp => Previous(state),
            UiKeyCode.Right or UiKeyCode.Down or UiKeyCode.PageDown => Next(state),
            UiKeyCode.Home => state.Disabled ? null : First(state),
            UiKeyCode.End => state.Disabled ? null : Last(state),
            _ => null,
        };

        if (target is null)
        {
            return false;
        }

        next = state with { Index = target.Value };
        return true;
    }
}
