# Harbor.Tui.Sixel

ANSI streaming renderer with Sixel image support. Extends `AnsiTuiRenderer`
so the chat feed looks identical to the default streaming UI; when the `read`
tool returns an image file path, the renderer emits a Sixel escape sequence so
Sixel-capable terminals draw the image inline.

## When to use

- You live in a Sixel-capable terminal: xterm + vt_sixel, wezterm, foot,
  mlterm, mintty, RLogin, alacritty (with `-sixel` fork).
- You want images (screenshots, diagrams, photos) to render inline in the
  chat history instead of as binary garbage or bare file paths.
- You want a low-overhead renderer (~1 MB RSS, same as Ansi) with one extra
  capability.

## Platform support

| OS      | Sixel-capable terminals                                |
|---------|--------------------------------------------------------|
| Linux   | xterm (`+vt_sixel`), wezterm, foot, mlterm, alacritty† |
| macOS   | wezterm, mlterm, RLogin, xterm (XQuartz + vt_sixel)    |
| Windows | wezterm, RLogin, mintty (recent)                       |
| Any     | Falls back to a `[image] <filename>` placeholder       |

> † Alacritty has no upstream Sixel support; the `alacritty_sixel` fork does.

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Tui.Abstractions`
- `Harbor.Tui.Ansi` (this renderer inherits from `AnsiTuiRenderer`)
- `Microsoft.Extensions.Logging.Abstractions`

The skeleton does not depend on `System.Drawing.Common` or `ImageSharp`. The
placeholder `SixelEncoder` produces a small solid-color band so Sixel-capable
terminals show something. For real image encoding, swap in `SixelSharp` or
P/Invoke `libsixel`.

## Files

- `Harbor.Tui.Sixel.csproj` — `net10.0`, references `Harbor.Tui.Ansi`.
- `SixelTuiRenderer.cs` — sealed class extending `AnsiTuiRenderer`. Overrides
  `RenderAsync` to intercept `ToolExecutionEndEvent` whose output contains an
  image path; emits a Sixel sequence before delegating to the base renderer.
- `SixelEncoder` — minimal PNG/JPEG → Sixel encoder (placeholder). Produces
  valid DCS sequences; production use should add a real raster decoder.

## How it works

```csharp
public override Task RenderAsync(AgentEvent @event, CancellationToken ct)
{
    if (@event is ToolExecutionEndEvent tee && !tee.IsError)
        TryRenderImageInline(tee);   // emits Sixel escape sequence

    return base.RenderAsync(@event, ct);  // normal Ansi streaming
}

private void TryRenderImageInline(ToolExecutionEndEvent tee)
{
    string? path = TryExtractImagePath(tee.Result.Output);
    if (path is not null && File.Exists(path))
    {
        byte[] bytes = File.ReadAllBytes(path);
        Console.Out.Write(_encoder.Encode(bytes, maxColors: 256, maxCols: 80));
        Console.Out.Flush();
    }
}
```

The chat history still streams normally; the Sixel block is appended between
the `→ read` tool-start line and the `✓` tool-end line.

## Build

```bash
# Restore + build (cross-platform, no extra workloads)
dotnet build src/Harbor.Tui.Sixel/Harbor.Tui.Sixel.csproj -c Release

# Run Harbor with the Sixel renderer (in a Sixel-capable terminal!)
HARBOR_TUI=sixel dotnet run --project src/Harbor.Cli/Harbor.Cli.csproj

# Then ask the agent: "show me the screenshot at /tmp/shot.png"
```

Verify your terminal supports Sixel:

```bash
echo -e '\x1bP0;0;0q\x1b\\'   # should show a tiny gap; no garbage
```

## Selecting this renderer

Set `HARBOR_TUI=sixel` in your environment, or add `tui: "sixel"` to
`~/.harbor/config.json`.

## Memory footprint

Same as `AnsiTuiRenderer`: ~1 MB RSS idle. The encoder allocates a small
buffer per image; large images are not yet downsampled before encoding.

## Limitations / TODO

- The encoder is a placeholder — it emits a 4-color 16×6 band, not the actual
  image pixels. Real encoding needs a PNG decoder + color quantizer.
- No terminal capability detection — emits Sixel unconditionally. Add a check
  for the `DA1` response (`\x1b[c`) and look for `;4;` in the reply to confirm
  Sixel support before emitting.
- No animated GIF support — Sixel can do animation via repeat directives but
  the skeleton doesn't generate them.
- No scaling — images larger than the terminal width will overflow. Need to
  downscale to fit `Console.WindowWidth`.
