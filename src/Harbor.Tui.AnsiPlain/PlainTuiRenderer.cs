namespace Harbor.Tui.AnsiPlain;

using Harbor.Tui.AnsiPlain.EscapeCodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
///     Plain mode of the unified renderer — no ANSI escape codes, no colors.
///     Use for: piping to other commands, CI logs, accessibility, files.
///     Thin subclass of <see cref="AnsiPlainTuiRenderer"/> selecting
///     <see cref="NullEscapeStrategy"/>; all render logic lives in the base.
/// </summary>
public sealed class PlainTuiRenderer : AnsiPlainTuiRenderer
{
    public PlainTuiRenderer(TextWriter? writer = null)
        : base(
            writer ?? Console.Out,
            ownsWriter: writer is not null,
            NullEscapeStrategy.Instance,
            NullLogger<PlainTuiRenderer>.Instance)
    {
    }
}
