using Harbor.Desktop.Abstractions.DesignSystem;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Shared cell styles for chat widgets — mapped to Harbor design tokens
/// so the terminal UI matches the desktop theme exactly.
/// </summary>
public static class ChatPalette
{
    private static PackedColor ToTerminalColor(RgbColor color) =>
        PackedColor.Rgb(color.R, color.G, color.B);

    // Primary semantic colors from ColorPalette, converted to terminal truecolor
    public static readonly CellStyle UserPrefix = new(ToTerminalColor(ColorPalette.MochaBlue), attrs: StyleAttr.Bold);
    public static readonly CellStyle UserText = new(attrs: StyleAttr.Bold);

    public static readonly CellStyle System = new(attrs: StyleAttr.Dim | StyleAttr.Italic);

    public static readonly CellStyle ToolName = new(attrs: StyleAttr.Bold);
    public static readonly CellStyle ToolArgs = new(ToTerminalColor(ColorPalette.MochaSubtext0));
    public static readonly CellStyle ToolRunning = new(ToTerminalColor(ColorPalette.MochaYellow));
    public static readonly CellStyle ToolOk = new(ToTerminalColor(ColorPalette.MochaGreen));
    public static readonly CellStyle ToolError = new(ToTerminalColor(ColorPalette.MochaRed));
    public static readonly CellStyle ToolBody = new(ToTerminalColor(ColorPalette.MochaSubtext0));

    public static readonly CellStyle Dim = new(attrs: StyleAttr.Dim);
}
