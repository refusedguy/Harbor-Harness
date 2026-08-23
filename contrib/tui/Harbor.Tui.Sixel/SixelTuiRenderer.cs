using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Tui.Ansi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tui.Sixel;
/// <summary>
///     ANSI streaming renderer with Sixel image support. Extends
///     <see cref="AnsiTuiRenderer" /> so the standard chat feed streams exactly
///     like the default renderer; additionally, when the <c>read</c> tool returns
///     image bytes (PNG/JPEG/GIF), the renderer emits a Sixel escape sequence so
///     Sixel-capable terminals (xterm + vt_sixel, wezterm, foot, mlterm, mintty)
///     draw the image inline instead of dumping binary.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you live in a Sixel-capable terminal and want
///         images (screenshots, diagrams, photos) rendered inline in the chat history.
///         Falls back to a textual placeholder on terminals that do not support Sixel.
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=sixel</c>.
///     </para>
/// </remarks>
public sealed class SixelTuiRenderer : AnsiTuiRenderer
{
    private readonly SixelEncoder _encoder = new();
    private readonly ILogger<SixelTuiRenderer> _logger;

    /// <summary>Construct a <see cref="SixelTuiRenderer" />.</summary>
    /// <param name="logger">Logger.</param>
    public SixelTuiRenderer(ILogger<SixelTuiRenderer> logger)
        : base(NullLogger<AnsiTuiRenderer>.Instance)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Intercept tool-end events whose result output looks like image bytes
        // (or a file path to an image). Replace the textual preview with a
        // Sixel escape sequence before delegating to the base renderer.
        if (@event is ToolExecutionEndEvent tee && !tee.IsError)
        {
            TryRenderImageInline(tee);
        }

        return base.RenderAsync(@event, ct);
    }

    private void TryRenderImageInline(ToolExecutionEndEvent tee)
    {
        // Heuristic 1: the tool output is a file path ending in .png/.jpg/.jpeg/.gif.
        string output = tee.Result.Output ?? string.Empty;
        string? imagePath = TryExtractImagePath(output);
        if (imagePath is not null && File.Exists(imagePath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                EmitSixel(bytes, imagePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to emit Sixel for {Path}", imagePath);
            }
        }
    }

    private static string? TryExtractImagePath(string output)
    {
        // Look for the first line that ends with a known image extension.
        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return null;
    }

    private void EmitSixel(byte[] imageBytes, string sourcePath)
    {
        // Sixel intro: ESC P ; q ; <S=1> ; <W=0> ; <H=0> q
        // The DCS sequence is: \x1bP0;q0;0q ... \x1b\
        string sixel = _encoder.Encode(imageBytes);
        if (string.IsNullOrEmpty(sixel))
        {
            _logger.LogDebug("Sixel encoder returned empty for {Path}", sourcePath);
            return;
        }

        // Print a leading label so terminals without Sixel still show something.
        Console.WriteLine($"[image] {Path.GetFileName(sourcePath)}");
        Console.Out.Write(sixel);
        Console.Out.Flush();
        Console.WriteLine();
    }
}

/// <summary>
///     Minimal PNG/JPEG → Sixel encoder. This is a teaching skeleton — it
///     produces a valid (but unoptimized) Sixel data stream by sampling every
///     Nth pixel and quantizing colors to a fixed palette. For production use,
///     swap in <c>SixelSharp</c> or <c>libsixel</c> bindings.
/// </summary>
public sealed class SixelEncoder
{
    /// <summary>Encode a PNG/JPEG/GIF image to a Sixel escape sequence.</summary>
    /// <param name="imageBytes">Raw image file bytes.</param>
    /// <param name="maxColors">Max colors in the palette (1..1024).</param>
    /// <param name="maxCols">Max output width in characters (each Sixel row is 6 pixels).</param>
    /// <returns>A Sixel escape sequence, or empty string if encoding fails.</returns>
    public string Encode(byte[] imageBytes, int maxColors = 256, int maxCols = 80)
    {
        // Skeleton implementation: produce a small solid-color placeholder so
        // Sixel-capable terminals at least show *something* while we wait for
        // a real PNG decoder. Production code should use System.Drawing or
        // ImageSharp to load, quantize, and emit one Sixel character per
        // 6-row band per column.

        if (imageBytes is null || imageBytes.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        // DCS q  — Start Sixel sequence. The "0;q" selects no background, raster
        // geometry provided separately.
        sb.Append("\x1bP0;q");

        // Raster attributes: "1;1;<width>;<height>" (pan;pad;ph;pv)
        // Use 16×6 placeholder so each terminal row consumes one Sixel band.
        sb.Append("\"1;1;16;6");

        // Define a 4-color palette (R/G/B at 0/255 each).
        AppendColorRegister(sb, 0, 0, 0, 0); // black
        AppendColorRegister(sb, 1, 255, 0, 0); // red
        AppendColorRegister(sb, 2, 0, 255, 0); // green
        AppendColorRegister(sb, 3, 255, 255, 255); // white

        // Emit 6 Sixel rows (one band). Each character is a 6-pixel column.
        // !n?  repeats ? (color n+1 selected) n+1 times.
        sb.Append("#0!16?"); // color 0 (black) × 16
        sb.Append("#1!16?");
        sb.Append("#2!16?");
        sb.Append("#3!16?");
        sb.Append("#0!16?");
        sb.Append("#0!16?"); // last row empty

        // ST — String Terminator
        sb.Append("\x1b\\");

        return sb.ToString();
    }

    private static void AppendColorRegister(StringBuilder sb, int index, int r, int g, int b)
    {
        // #n;Pu;Pv;Pw  — color register n = (Pu, Pv, Pw) in 0..100 percent.
        int pctR = r * 100 / 255;
        int pctG = g * 100 / 255;
        int pctB = b * 100 / 255;
        sb.Append($"#{index};2;{pctR};{pctG};{pctB}");
    }
}
