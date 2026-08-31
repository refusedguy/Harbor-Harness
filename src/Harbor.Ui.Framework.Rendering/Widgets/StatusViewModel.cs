using System.Globalization;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

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

    /// <summary>Retry countdown line ("retry 2/3 in 4s") — a fixed-priority
    /// Warning segment while the host feeds it; null when no retry is pending.
    /// Set once per change (precomputed via <see cref="RetryCountdown.Line" />),
    /// never interpolated per frame.</summary>
    public string? Retry { get; set; }

    public StatusBarMode Mode { get; set; }

    /// <summary>Fine-grained agent phase (mascot-brand T1): disambiguates
    /// Running into thinking / tool-call and flags end-of-run outcomes for the
    /// mascot. <see cref="AgentPhase.Auto" /> derives from <see cref="Mode" /> alone.</summary>
    public AgentPhase Phase { get; set; }

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

    /// <summary>Token-only pull feed (host polling <c>ITokenTracker.GetStats()</c>):
    /// refreshes the token segment and PRESERVES any cost a richer source
    /// already reported — pull feeds must never erase event-pushed cost.</summary>
    public void SetUsage(long inputTokens, long outputTokens)
    {
        Tokens = FormatCount(inputTokens) + "↑ " + FormatCount(outputTokens) + "↓";
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

        if (!string.IsNullOrEmpty(Retry))
        {
            workspace[n++] = new StatusSeg(Retry!, StatusAccent.Warning, FixedPriority: true);
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

    /// <summary>Precomputed bars — BuildSegments stays allocation-free.</summary>
    private static readonly string[] BarCache =
    [
        new string(Empty, CtxCells),
        new string(Filled, 1) + new string(Empty, CtxCells - 1),
        new string(Filled, 2) + new string(Empty, CtxCells - 2),
        new string(Filled, 3) + new string(Empty, CtxCells - 3),
        new string(Filled, 4) + new string(Empty, CtxCells - 4),
        new string(Filled, 5) + new string(Empty, CtxCells - 5),
        new string(Filled, CtxCells),
    ];

    internal static string ContextBar(double ratio)
    {
        int filled = (int)Math.Round(ratio * CtxCells, MidpointRounding.AwayFromZero);
        return BarCache[Math.Clamp(filled, 0, CtxCells)];
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
