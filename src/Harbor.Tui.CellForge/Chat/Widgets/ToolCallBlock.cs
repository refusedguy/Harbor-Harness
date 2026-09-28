using System.Text;
using Harbor.Tui.CellForge.Rendering;
using FrameworkStatusMappers = Harbor.Ui.Framework.Converters.StatusMappers;
using VmToolCallStatus = Harbor.Ui.Framework.ViewModels.ToolCallStatus;
using VmToolCall = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Execution phase of a tool card.</summary>
public enum ToolCallStatus : byte
{
    Running = 0,
    Ok = 1,
    Error = 2,
}

/// <summary>
/// Identity of the call: stable id + display name + truncated args.
/// CF-E-011: optional diff-preview surface filled from
/// <see cref="Rendering.DiffPreview"/> — <c>FilePath</c> extracted from the
/// args payload, <c>DiffPreview</c> the 6-line inline preview, <c>DiffFull</c>
/// the full (≤80-line) diff backing the expand path. All optional so existing
/// call sites stay source-compatible.
/// Tool cards: <c>ArgsFull</c> carries the complete single-line args payload
/// for the expanded view; <c>ArgsSummary</c> stays the ≤48-char header slice.
/// </summary>
public readonly record struct ToolCallInfo(
    string Id,
    string ToolName,
    string ArgsSummary,
    string? FilePath = null,
    string? DiffPreview = null,
    string? DiffFull = null,
    string? ArgsFull = null);

/// <summary>
/// Final outcome of a tool execution. <see cref="DiffText"/> carries a unified
/// diff when the producing tool supplied one — the feed upgrades to a
/// <c>DiffBlock</c>-style body in that case; otherwise output lines show.
/// </summary>
public sealed class ToolResultBody
{
    public ToolResultBody(string output, bool isError, TimeSpan duration, string? diffText = null)
    {
        Output = output ?? string.Empty;
        IsError = isError;
        Duration = duration;
        DiffText = diffText;
    }

    public string Output { get; }
    public bool IsError { get; }
    public TimeSpan Duration { get; }
    public string? DiffText { get; }

    /// <summary>
    /// Back-compat shim over <see cref="FrameworkStatusMappers.DurationToText"/>.
    /// Kept (not deleted, CF-E-012) because
    /// <c>ChatBlockTests.FormatDuration_HumanBuckets</c> pins the legacy
    /// <c>"&lt;1ms"</c> bucket for sub-millisecond durations, while
    /// <c>DurationToText</c> returns <see cref="string.Empty"/> there
    /// (instantaneous calls hide the duration column). Internal paint paths
    /// call <c>DurationToText</c> directly.
    /// </summary>
    public static string FormatDuration(TimeSpan d) =>
        FrameworkStatusMappers.DurationToText(d) is { Length: > 0 } text ? text : "<1ms";
}

/// <summary>
/// Mutable tool-call card (widgets §3.1): created Running on ToolCallStart,
/// completed Ok/Error with duration on ToolExecutionEnd. The timeline marks
/// its slot dirty after each mutation — paint itself is pure over fields.
/// </summary>
public sealed class ToolCallBlock : ICollapsibleChatBlock
{
    private const char RunningGlyph = '⚙';
    private const char OkGlyph = '✔';
    private const char ErrorGlyph = '✖';

    private ToolCallStatus _status;
    private ToolResultBody? _body;

    public ToolCallBlock(in ToolCallInfo info)
    {
        Info = info;
        _status = ToolCallStatus.Running;
        MaxBodyLines = ICollapsibleChatBlock.DefaultCollapsedBodyLines;
    }

    public ToolCallInfo Info { get; }

    public ToolCallStatus Status => _status;

    public ToolResultBody? Body => _body;

    /// <summary>
    /// Framework-level <see cref="VmToolCall"/> snapshot for this block.
    /// Bridges the pure-paint widget into the shared
    /// <c>Harbor.Ui.Framework.ViewModels</c> vocabulary so renderers can
    /// bind against the same status / duration / diff surface used by
    /// Avalonia / WPF / Blazor.
    /// </summary>
    public VmToolCall ViewModel => new()
    {
        Id = Info.Id,
        ToolName = Info.ToolName,
        ArgsPreview = Info.ArgsSummary,
        Status = _status switch
        {
            ToolCallStatus.Ok => VmToolCallStatus.Success,
            ToolCallStatus.Error => VmToolCallStatus.Error,
            _ => VmToolCallStatus.Running,
        },
        ResultPreview = _body is null ? string.Empty : _body.Output,
        Duration = _body?.Duration ?? TimeSpan.Zero,
        IsDiffTool = Info.DiffFull is not null,
        DiffFilePath = Info.FilePath,
        DiffPreview = Info.DiffPreview,
        DiffFull = Info.DiffFull,
    };

    /// <summary>
    /// Status in the shared <c>Harbor.Ui.Framework.ViewModels</c> vocabulary —
    /// the single bridge that lets the framework-free <c>StatusMappers</c>
    /// converters operate on this block. Local <c>Ok</c> maps to
    /// <c>Success</c> (naming only; same meaning).
    /// </summary>
    private VmToolCallStatus ViewModelStatus => _status switch
    {
        ToolCallStatus.Ok => VmToolCallStatus.Success,
        ToolCallStatus.Error => VmToolCallStatus.Error,
        _ => VmToolCallStatus.Running,
    };

    /// <summary>
    /// Short status pill label via <c>StatusMappers.ToolCallStatusToPill</c>
    /// (<c>"running"</c> / <c>"ok"</c> / <c>"err"</c>).
    /// </summary>
    public string StatusPill => FrameworkStatusMappers.ToolCallStatusToPill(ViewModelStatus);

    /// <summary>
    /// Resource key for the status brush via
    /// <c>StatusMappers.ToolCallStatusToBrushKey</c> (e.g. <c>"MochaGreen"</c>).
    /// Consumed by projector-level style mapping (CF-D-005); cell paint keeps
    /// using <c>ChatPalette</c> styles (the single cell source of truth).
    /// </summary>
    public string StatusBrushKey => FrameworkStatusMappers.ToolCallStatusToBrushKey(ViewModelStatus);

    /// <summary>
    /// Collapsed-body line budget (continuation marker when exceeded).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; }

    /// <summary>
    /// Expanded result line budget (overflow marker when exceeded).
    /// Mirrors <c>ICollapsibleChatBlock.DefaultExpandedBodyLines</c> (kept as
    /// a const: <c>ToolCallCardTests</c> pins it for buffer sizing).
    /// </summary>
    public const int ExpandedBodyLines = 20;

    /// <summary>
    /// Whether the card is expanded (feed Enter/click toggles via
    /// <see cref="ToggleExpanded"/>). Satisfies the <c>ICollapsibleChatBlock</c>
    /// mixin contract; collapsed paint stays byte-identical to
    /// the pre-card layout (header + <see cref="MaxBodyLines"/> body preview);
    /// expanded adds the full-args row plus the result up to
    /// <see cref="ExpandedBodyLines"/> lines with a <c>…</c> overflow marker.
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click path).</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    /// <summary>Live one-line header suffix for task cards ([UX5] #265): the
    /// current child tool while running (<c>› read</c>), the retry fraction
    /// (<c>↻ read 1/3</c>), or the finished tally (<c>· 3 toolcalls</c>).
    /// Null/empty hides the suffix — non-task cards never set it, so their
    /// header paint stays byte-identical.</summary>
    public string? LiveSuffix { get; set; }

    /// <summary>True while <see cref="LiveSuffix"/> reports a retry — painted
    /// in <c>ChatPalette.ToolError</c> (red) instead of dim.</summary>
    public bool LiveSuffixIsError { get; set; }

    /// <summary>Full args text for the expanded row (falls back to the short summary).</summary>
    public string ArgsFullText => Info.ArgsFull ?? Info.ArgsSummary;

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    public string Kind => "tool-call";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 96 + (Info.ToolName.Length * 2) + (Info.ArgsSummary.Length * 2)
        + ((Info.ArgsFull?.Length ?? 0) * 2)
        + ((LiveSuffix?.Length ?? 0) * 2)
        + (_body is null ? 0 : 64 + (_body.Output.Length * 2));

    /// <summary>Completes the card; idempotent — first result wins.</summary>
    public void Complete(ToolResultBody body)
    {
        if (_body is not null)
        {
            return;
        }

        _body = body;
        _status = body.IsError ? ToolCallStatus.Error : ToolCallStatus.Ok;
    }

    public BlockMeasure Measure(int width)
    {
        int lines = 1;
        if (IsExpanded && !string.IsNullOrEmpty(ArgsFullText))
        {
            lines += 1; // full-args row
        }

        if (_body is not null)
        {
            // Mirror Paint: a present DiffText replaces the output body.
            if (_body.DiffText is not null)
            {
                lines += DiffLineCount();
            }
            else
            {
                lines += IsExpanded ? ExpandedBodyLineCount() : BodyLineCount();
            }
        }

        return BlockMeasure.Exact(lines);
    }

    public int CheapEstimate(int width)
    {
        int lines = 1;
        if (IsExpanded && !string.IsNullOrEmpty(ArgsFullText))
        {
            lines += 1;
        }

        if (_body is not null)
        {
            if (_body.DiffText is not null)
            {
                lines += DiffLineCount();
            }
            else
            {
                lines += IsExpanded ? ExpandedBodyLineCount() : BodyLineCount();
            }
        }

        return Math.Max(1, lines);
    }

    /// <summary>
    /// Click hit-test for the card header (mirrors
    /// <c>ApprovalGateView.TryHitDecision</c>): true when the card has painted
    /// and (<paramref name="col"/>, <paramref name="row"/>) lands on its
    /// header row. Coordinates are screen cells (same space as
    /// <c>MouseEvent.Column</c> / <c>Row</c>).
    /// </summary>
    public bool TryHitHeader(int col, int row)
    {
        if (_lastPaintRect is not { } rect || _lastSkipRows != 0)
        {
            return false;
        }

        return row == rect.Y && col >= rect.X && col < rect.X + rect.Width;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        var buffer = ctx.Buffer;
        int y = ctx.Rect.Y;
        PaintHeader(buffer, ctx.Rect.X, y, ctx.Rect.Width);

        bool showArgs = IsExpanded && !string.IsNullOrEmpty(ArgsFullText);
        if (_body is null)
        {
            if (showArgs && ctx.Rect.Bottom - (y + 1) > 0)
            {
                PaintArgsRow(buffer, ctx.Rect.X, y + 1, ctx.Rect.Width);
            }

            return;
        }

        y++;
        if (showArgs)
        {
            if (ctx.Rect.Bottom - y <= 0)
            {
                return;
            }

            PaintArgsRow(buffer, ctx.Rect.X, y, ctx.Rect.Width);
            y++;
        }

        int rows = ctx.Rect.Bottom - y;
        if (_body.DiffText is not null)
        {
            DiffRenderer.RenderPlain(_body.DiffText, buffer, ctx.Rect.X, y, rows);
            return;
        }

        PaintOutputBody(buffer, ctx.Rect.X, y, rows, IsExpanded ? ExpandedBodyLines : MaxBodyLines);
    }

    private void PaintHeader(ScreenBuffer buffer, int x, int y, int width)
    {
        if (width <= 0 || y >= buffer.Rows)
        {
            return;
        }

        char glyph = _status switch
        {
            ToolCallStatus.Ok => OkGlyph,
            ToolCallStatus.Error => ErrorGlyph,
            _ => RunningGlyph,
        };
        var glyphStyle = _status switch
        {
            ToolCallStatus.Ok => ChatPalette.ToolOk,
            ToolCallStatus.Error => ChatPalette.ToolError,
            _ => ChatPalette.ToolRunning,
        };

        buffer.SetText(x, y, [glyph], glyphStyle);
        int cursor = x + 1;
        if (cursor >= x + width)
        {
            return;
        }

        buffer.SetText(cursor, y, " ", CellStyle.Plain);
        cursor++;
        buffer.SetText(cursor, y, Info.ToolName, ChatPalette.ToolName);
        cursor += Info.ToolName.Length;

        if (_body is not null)
        {
            // CF-E-012: duration straight from StatusMappers.DurationToText.
            // It returns string.Empty for sub-millisecond (instantaneous) calls,
            // so the column hides instead of rendering " ()".
            var durationText = FrameworkStatusMappers.DurationToText(_body.Duration);
            if (durationText.Length > 0)
            {
                var tail = $" ({durationText})";
                buffer.SetText(cursor, y, tail, ChatPalette.Dim);
                cursor += tail.Length;
            }

            // Status pill (CF-E-012): painted only once completed, so the Running
            // header stays byte-identical (pinned by StreamingTests +
            // screenshot baselines). StatusPill still reports "running" pre-flight.
            // No ChatPalette.ToolPill: ChatPalette lives in Harbor.DesignSystem
            // (out of scope) — the pill reuses the status glyph style instead.
            var pill = $" [{StatusPill}]";
            buffer.SetText(cursor, y, pill, glyphStyle);
            cursor += pill.Length;
        }

        if (!string.IsNullOrEmpty(Info.ArgsSummary))
        {
            const string sep = "  ";
            int avail = (x + width) - cursor - sep.Length;
            if (avail > 0)
            {
                int shown = Math.Min(avail, Info.ArgsSummary.Length);
                buffer.SetText(cursor + sep.Length, y, Info.ArgsSummary.AsSpan(0, shown), ChatPalette.ToolArgs);
                cursor += sep.Length + shown;
            }
        }

        // [UX5] #265: task-card live suffix — current child tool / retry (red)
        // / finished tally. Null on every other card: no paint, no measure
        // change, existing baselines stay byte-identical.
        if (!string.IsNullOrEmpty(LiveSuffix))
        {
            const string suffixSep = "  ";
            int suffixAvail = (x + width) - cursor - suffixSep.Length;
            if (suffixAvail > 0)
            {
                var slice = LiveSuffix.AsSpan(0, Math.Min(suffixAvail, LiveSuffix.Length));
                buffer.SetText(cursor + suffixSep.Length, y, slice,
                    LiveSuffixIsError ? ChatPalette.ToolError : ChatPalette.Dim);
            }
        }
    }

    private void PaintArgsRow(ScreenBuffer buffer, int x, int y, int width)
    {
        if (width <= 0 || y >= buffer.Rows)
        {
            return;
        }

        string text = "  args: " + ArgsFullText;
        int avail = Math.Max(0, buffer.Cols - x);
        if (text.Length > avail)
        {
            text = text[..avail];
        }

        buffer.SetText(x, y, text, ChatPalette.ToolArgs);
    }

    /// <summary>
    /// Body paint delegated to the <c>ICollapsibleChatBlock</c> mixin
    /// (PRIM1a #291): geometry in <c>PaintBodyLines</c> (incl. the
    /// [UX5] #265 zero-budget law), colors caller-side.
    /// Byte-identical to the pre-mixin loop.
    /// </summary>
    private void PaintOutputBody(ScreenBuffer buffer, int x, int y, int rows, int maxLines)
    {
        var style = _body!.IsError ? ChatPalette.ToolError : ChatPalette.ToolBody;
        ICollapsibleChatBlock.PaintBodyLines(buffer, x, y, rows, _body.Output, maxLines, style, ChatPalette.Dim);
    }

    private int BodyLineCount()
    {
        if (MaxBodyLines <= 0)
        {
            return 0; // [UX5] #265: zero collapse budget = pure one-liner, not even a marker.
        }

        var output = _body!.Output.AsSpan().TrimEnd('\n');
        return ICollapsibleChatBlock.ClampedBodyLineCount(output, MaxBodyLines);
    }

    private int ExpandedBodyLineCount()
    {
        var output = _body!.Output.AsSpan().TrimEnd('\n');
        return ICollapsibleChatBlock.ClampedBodyLineCount(output, ExpandedBodyLines);
    }

    private int DiffLineCount() => DiffRenderer.CountLines(_body!.DiffText!);

    public string RawText()
    {
        var sb = new StringBuilder();
        sb.Append(RunningGlyph).Append(' ').Append(Info.ToolName);
        if (_body is not null)
        {
            sb.Append(" → ").Append(_body.IsError ? "error" : "ok")
              .Append(' ').Append(ToolResultBody.FormatDuration(_body.Duration));
        }

        return sb.ToString();
    }

    public bool HasDiffText => _body?.DiffText is not null;
}

/// <summary>
/// Minimal unified-diff blitter used by tool-card bodies before the dedicated
/// <c>DiffBlock</c> lands (W2.3): sign + colored line, no gutter numbers, no
/// syntax overlay. Pure functions over the diff text.
/// CF-E-011: inline preview budget mirrors <c>HdsDiffCompact.MaxLines</c>
/// (Avalonia) — at most <see cref="DiffPreview.MaxPreviewLines"/> content rows,
/// then a single <c>"… diff truncated"</c> overflow row, so an unbounded
/// foreign <c>DiffText</c> can no longer blow the card's line budget
/// (<see cref="DiffPreview.DiffTruncatedSentinel"/> is the single source of
/// truth for the marker text).
/// </summary>
internal static class DiffRenderer
{
    internal const int MaxPreviewLines = DiffPreview.MaxPreviewLines;

    internal const string TruncatedMarker = DiffPreview.DiffTruncatedSentinel;

    public static int CountLines(string diffText)
    {
        int count = 0;
        var rest = diffText.AsSpan();
        while (!rest.IsEmpty)
        {
            int nl = rest.IndexOf('\n');
            count++;
            rest = nl < 0 ? default : rest[(nl + 1)..];
        }

        return count > MaxPreviewLines ? MaxPreviewLines + 1 : count;
    }

    public static void RenderPlain(string diffText, ScreenBuffer buffer, int x, int y, int maxRows)
    {
        var rest = diffText.AsSpan();
        int row = 0;
        int shown = 0;
        while (!rest.IsEmpty && row < maxRows && shown < MaxPreviewLines)
        {
            int nl = rest.IndexOf('\n');
            var line = nl < 0 ? rest : rest[..nl];
            rest = nl < 0 ? default : rest[(nl + 1)..];

            char sign = line.IsEmpty ? ' ' : line[0];
            var style = sign switch
            {
                '+' => ChatPalette.ToolOk,
                '-' => ChatPalette.ToolError,
                '@' => new CellStyle(PackedColor.Indexed(6)),
                _ => ChatPalette.ToolBody,
            };

            int avail = Math.Max(0, buffer.Cols - x);
            if (line.Length > avail)
            {
                line = line[..avail];
            }

            buffer.SetText(x, y + row, line, style);
            row++;
            shown++;
        }

        // Overflow marker when the preview budget (not the viewport) cut
        // content — the same trailing row HdsDiffCompact appends past MaxLines.
        if (!rest.IsEmpty && row < maxRows)
        {
            buffer.SetText(x, y + row, TruncatedMarker, ChatPalette.Dim);
        }
    }
}
