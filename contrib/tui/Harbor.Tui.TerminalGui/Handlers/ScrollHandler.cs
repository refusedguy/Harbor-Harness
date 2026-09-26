using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Tui.TerminalGui.Handlers;
/// <summary>
///     Pure scroll math over <see cref="UiState" />. Mirrors SpectreTui's
///     semantics: scroll is rows-from-bottom (0 = live tail), grows toward
///     the top, clamped by the reducer.
/// </summary>
public static class ScrollHandler
{
    /// <summary>Maximum legal scroll offset for the given state.</summary>
    public static int MaxScroll(UiState s) =>
        Math.Max(0, s.TotalLines - Math.Max(1, s.ViewportLines));

    /// <summary>Visible slice of <see cref="UiState.Lines" /> given the current scroll offset.</summary>
    public static IEnumerable<ChatLine> VisibleSlice(UiState s)
    {
        if (s.Lines.IsDefaultOrEmpty)
            yield break;

        int total = s.Lines.Length;
        int viewport = Math.Max(1, s.ViewportLines);
        int bottom = total;
        int top = Math.Max(0, bottom - viewport - s.ScrollOffset);
        int count = bottom - s.ScrollOffset - top;
        for (int i = 0; i < count; i++)
            yield return s.Lines[top + i];
    }

    /// <summary>Footer text: <c>scroll 42%</c>.</summary>
    public static string ScrollPercent(UiState s)
    {
        int max = MaxScroll(s);
        if (max == 0)
            return "scroll 0%";
        return $"scroll {s.ScrollOffset * 100 / max}%";
    }
}
