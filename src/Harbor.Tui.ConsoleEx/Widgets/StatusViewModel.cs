using System.Globalization;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Typed status payload (widgets §3.7, grok StatusLineContext): «нет данных ⇒
/// None, не ноль» — unknown token counts mean the context segment is absent,
/// never zero-filled. <see cref="BuildSegments"/> packs a reusable workspace
/// span; no string interpolation per frame.
/// </summary>
public sealed class StatusViewModel
{
    /// <summary>Model id — fixed-priority segment, survives truncation.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Cumulative cost text ("$0.0031") — rightmost, dies first.</summary>
    public string? Cost { get; set; }

    /// <summary>Token totals text ("12.3k↑ 4.5k↓") — second from the right.</summary>
    public string? Tokens { get; set; }

    public StatusBarMode Mode { get; set; }

    public int? ContextTokensUsed { get; private set; }

    public int ContextWindow { get; private set; }

    /// <summary>grok None-semantics: false when the provider reported nothing.</summary>
    public bool TryGetContextTokens(out int used)
    {
        used = ContextTokensUsed ?? 0;
        return ContextTokensUsed.HasValue && ContextWindow > 0;
    }

    public void SetContext(int usedTokens, int windowTokens)
    {
        ContextTokensUsed = usedTokens;
        ContextWindow = windowTokens;
    }

    public void ClearContext() => ContextTokensUsed = null;

    /// <summary>Formats usage numbers once per change, not per frame.</summary>
    public void SetUsage(long inputTokens, long outputTokens, decimal? costUsd)
    {
        Tokens = FormatCount(inputTokens) + "↑ " + FormatCount(outputTokens) + "↓";
        Cost = costUsd is null ? null : "$" + costUsd.Value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Fills <paramref name="workspace"/> left-to-right; returns segment count.</summary>
    public int BuildSegments(Span<StatusSeg> workspace)
    {
        int n = 0;
        if (!string.IsNullOrEmpty(Model))
        {
            workspace[n++] = new StatusSeg(Model, StatusAccent.Accent, FixedPriority: true);
        }

        switch (Mode)
        {
            case StatusBarMode.AwaitingApproval:
                workspace[n++] = new StatusSeg("⏸ awaiting approval", StatusAccent.Warning, FixedPriority: true);
                break;
            case StatusBarMode.Compacting:
                workspace[n++] = new StatusSeg("compacting…", StatusAccent.Dim, FixedPriority: true);
                break;
        }

        if (TryGetContextTokens(out var used))
        {
            double ratio = Math.Clamp((double)used / ContextWindow, 0, 1);
            var accent = ratio >= CtxDangerThreshold ? StatusAccent.Error
                : ratio >= CtxWarnThreshold ? StatusAccent.Warning
                : StatusAccent.Success;
            workspace[n++] = new StatusSeg(ContextBar(ratio), accent, FixedPriority: false);
        }

        if (!string.IsNullOrEmpty(Tokens))
        {
            workspace[n++] = new StatusSeg(Tokens!, StatusAccent.Dim, FixedPriority: false);
        }

        if (!string.IsNullOrEmpty(Cost))
        {
            workspace[n++] = new StatusSeg(Cost!, StatusAccent.Dim, FixedPriority: false);
        }

        return n;
    }

    public const double CtxWarnThreshold = 0.50;
    public const double CtxDangerThreshold = 0.85;
    public const int CtxCells = 6;
    private const char Filled = '▰';
    private const char Empty = '▱';

    internal static string ContextBar(double ratio)
    {
        int filled = (int)Math.Round(ratio * CtxCells, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, CtxCells);
        return new string(Filled, filled) + new string(Empty, CtxCells - filled);
    }

    internal static string FormatCount(long v) => v switch
    {
        >= 1_000_000 => (v / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (v / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => v.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Blits fitted segments into a buffer row: accent colors, single-space gaps.</summary>
public static class StatusBarWidget
{
    public static CellStyle StyleOf(StatusAccent accent) => accent switch
    {
        StatusAccent.Dim => ChatPalette.Dim,
        StatusAccent.Accent => new CellStyle(PackedColor.Indexed(4), attrs: StyleAttr.Bold),
        StatusAccent.Success => new CellStyle(PackedColor.Indexed(2)),
        StatusAccent.Warning => new CellStyle(PackedColor.Indexed(3)),
        StatusAccent.Error => new CellStyle(PackedColor.Indexed(1)),
        _ => CellStyle.Plain,
    };

    public static void Paint(ScreenBuffer buffer, Rect rect, ReadOnlySpan<StatusSeg> segs)
    {
        int x = rect.X;
        int end = rect.Right;
        for (int i = 0; i < segs.Length && x < end; i++)
        {
            if (i > 0)
            {
                buffer.SetText(x++, rect.Y, " ", CellStyle.Plain);
            }

            var style = StyleOf(segs[i].Accent);
            buffer.SetText(x, rect.Y, segs[i].Text.AsSpan(), style);
            x += SegWidth.Of(segs[i].Text);
        }
    }
}
