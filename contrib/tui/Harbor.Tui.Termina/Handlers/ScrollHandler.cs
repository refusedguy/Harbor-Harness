using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Tui.Termina.Handlers;
/// <summary>
///     Pure scroll math over <see cref="UiState" />. Mirrors SpectreTui's
///     <c>ChatViewProjector</c> semantics: scroll is rows-from-bottom
///     (0 = live tail), grows toward the top, clamped by the reducer.
/// </summary>
public static class ScrollHandler
{
    /// <summary>Maximum legal scroll offset for the given state.</summary>
    public static int MaxScroll(UiState s) =>
        Math.Max(0, s.Ui.TotalLines - Math.Max(1, s.Ui.ViewportLines));

    /// <summary>Visible slice of <see cref="UiState.Chat.Lines" /> given the current scroll offset.</summary>
    public static IEnumerable<ChatLine> VisibleSlice(UiState s)
    {
        if (s.Chat.Lines.IsDefaultOrEmpty)
            yield break;

        int total = s.Chat.Lines.Length;
        int viewport = Math.Max(1, s.Ui.ViewportLines);
        int bottom = total; // exclusive end
        int top = Math.Max(0, bottom - viewport - s.Ui.ScrollOffset);
        int count = bottom - s.Ui.ScrollOffset - top;
        for (int i = 0; i < count; i++)
            yield return s.Chat.Lines[top + i];
    }

    /// <summary>Footer text shown in the status area: <c>scroll 42%</c>.</summary>
    public static string ScrollPercent(UiState s)
    {
        int max = MaxScroll(s);
        if (max == 0)
            return "scroll 0%";
        return $"scroll {s.Ui.ScrollOffset * 100 / max}%";
    }
}
