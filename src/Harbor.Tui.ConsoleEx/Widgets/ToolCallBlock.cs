using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>Execution phase of a tool card.</summary>
public enum ToolCallStatus : byte
{
    Running = 0,
    Ok = 1,
    Error = 2,
}

/// <summary>Identity of the call: stable id + display name + truncated args.</summary>
public readonly record struct ToolCallInfo(string Id, string ToolName, string ArgsSummary);

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

    public static string FormatDuration(TimeSpan d) => d.TotalMilliseconds switch
    {
        < 1 => "<1ms",
        < 1000 => $"{(int)d.TotalMilliseconds}ms",
        _ => $"{d.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s",
    };
}

/// <summary>
/// Mutable tool-call card (widgets §3.1): created Running on ToolCallStart,
/// completed Ok/Error with duration on ToolExecutionEnd. The timeline marks
/// its slot dirty after each mutation — paint itself is pure over fields.
/// </summary>
public sealed class ToolCallBlock : IChatBlock
{
    private const char RunningGlyph = '⚙';
    private const char OkGlyph = '✔';
    private const char ErrorGlyph = '✖';
    private const int DefaultBodyLines = 4;

    private ToolCallStatus _status;
    private ToolResultBody? _body;

    public ToolCallBlock(in ToolCallInfo info)
    {
        Info = info;
        _status = ToolCallStatus.Running;
        MaxBodyLines = DefaultBodyLines;
    }

    public ToolCallInfo Info { get; }

    public ToolCallStatus Status => _status;

    public ToolResultBody? Body => _body;

    /// <summary>Collapsed-body line budget (continuation marker when exceeded).</summary>
    public int MaxBodyLines { get; set; }

    public string Kind => "tool-call";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 96 + (Info.ToolName.Length * 2) + (Info.ArgsSummary.Length * 2)
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
        if (_body is not null)
        {
            lines += BodyLineCount();
            if (_body.DiffText is not null)
            {
                lines += DiffLineCount();
            }
        }

        return BlockMeasure.Exact(lines);
    }

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int y = ctx.Rect.Y;
        PaintHeader(buffer, ctx.Rect.X, y, ctx.Rect.Width);

        if (_body is null)
        {
            return;
        }

        y++;
        int rows = ctx.Rect.Bottom - y;
        if (_body.DiffText is not null)
        {
            DiffRenderer.RenderPlain(_body.DiffText, buffer, ctx.Rect.X, y, rows);
            return;
        }

        PaintOutputBody(buffer, ctx.Rect.X, y, rows);
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
            var tail = $" ({ToolResultBody.FormatDuration(_body.Duration)})";
            buffer.SetText(cursor, y, tail, ChatPalette.Dim);
            cursor += tail.Length;
        }

        if (!string.IsNullOrEmpty(Info.ArgsSummary))
        {
            const string sep = "  ";
            int avail = (x + width) - cursor - sep.Length;
            if (avail > 0)
            {
                var args = Info.ArgsSummary.AsSpan(0, Math.Min(avail, Info.ArgsSummary.Length));
                buffer.SetText(cursor + sep.Length, y, args, ChatPalette.ToolArgs);
            }
        }
    }

    private void PaintOutputBody(ScreenBuffer buffer, int x, int y, int rows)
    {
        var output = _body!.Output.AsSpan().TrimEnd('\n');
        if (output.IsEmpty || rows <= 0)
        {
            return;
        }

        var style = _body.IsError ? ChatPalette.ToolError : ChatPalette.ToolBody;
        int shown = 0;
        int cursorY = y;
        var rest = output;
        while (!rest.IsEmpty && shown < MaxBodyLines && shown < rows)
        {
            int nl = rest.IndexOf('\n');
            var line = nl < 0 ? rest : rest[..nl];
            rest = nl < 0 ? default : rest[(nl + 1)..];

            int avail = Math.Max(0, buffer.Cols - x);
            if (line.Length > avail)
            {
                line = line[..avail];
            }

            buffer.SetText(x, cursorY, line, style);
            cursorY++;
            shown++;
        }

        // Continuation marker when both the collapse budget and truncation cut content.
        bool moreLines = !rest.IsEmpty;
        bool moreCols = output.Length > 0 && CountLogicalLines(output) > shown;
        if ((moreLines || moreCols) && shown < rows)
        {
            buffer.SetText(x, cursorY, "…", ChatPalette.Dim);
        }
    }

    private static int CountLogicalLines(ReadOnlySpan<char> text)
    {
        int count = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private int BodyLineCount()
    {
        var output = _body!.Output.AsSpan().TrimEnd('\n');
        if (output.IsEmpty)
        {
            return 0;
        }

        int logical = CountLogicalLines(output);
        return Math.Min(logical, MaxBodyLines) + (logical > MaxBodyLines ? 1 : 0);
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
}

/// <summary>
/// Minimal unified-diff blitter used by tool-card bodies before the dedicated
/// <c>DiffBlock</c> lands (W2.3): sign + colored line, no gutter numbers, no
/// syntax overlay. Pure functions over the diff text.
/// </summary>
internal static class DiffRenderer
{
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

        return count;
    }

    public static void RenderPlain(string diffText, ScreenBuffer buffer, int x, int y, int maxRows)
    {
        var rest = diffText.AsSpan();
        int row = 0;
        while (!rest.IsEmpty && row < maxRows)
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
        }
    }
}
