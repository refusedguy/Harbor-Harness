using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Shared cell styles for chat widgets — the single place where block colors
/// are decided, so goldens and themes stay consistent. 16-color indexed
/// palette only: readable everywhere after AnsiWriter downgrade.
/// </summary>
public static class ChatPalette
{
    public static readonly CellStyle UserPrefix = new(PackedColor.Indexed(4), attrs: StyleAttr.Bold);

    public static readonly CellStyle UserText = new(attrs: StyleAttr.Bold);

    public static readonly CellStyle System = new(attrs: StyleAttr.Dim | StyleAttr.Italic);

    public static readonly CellStyle ToolName = new(attrs: StyleAttr.Bold);

    public static readonly CellStyle ToolArgs = new(PackedColor.Indexed(8));

    public static readonly CellStyle ToolRunning = new(PackedColor.Indexed(3));

    public static readonly CellStyle ToolOk = new(PackedColor.Indexed(2));

    public static readonly CellStyle ToolError = new(PackedColor.Indexed(1));

    public static readonly CellStyle ToolBody = new(PackedColor.Indexed(8));

    public static readonly CellStyle Dim = new(attrs: StyleAttr.Dim);
}
