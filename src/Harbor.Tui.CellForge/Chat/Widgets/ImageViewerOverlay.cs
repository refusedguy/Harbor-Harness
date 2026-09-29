using Harbor.Ui.Framework.Rendering;

using CSharpFunctionalExtensions;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Fullscreen image zoom viewer (KILLER_FEATURES §2.7 Feature 12, issue #387):
/// the same overlay/zoom primitives as the diff viewer, pointed at an
/// <see cref="ImageBlock" /> instead of a unified diff.
///
/// <para><b>Contract.</b> Modal and opaque — it occludes the chat beneath it
/// and forms an input barrier, so keys never reach the agent while it is open
/// (the host routes through <see cref="ImageViewerOverlayLayer.OnKey" /> and
/// swallows the rest). <see cref="Show" /> snapshots nothing but the block,
/// the viewer owns no scroll, no selection and no <c>UiState</c> field, so
/// closing it restores the feed by construction — the timeline never moved.</para>
///
/// <para><b>Zoom.</b> A percentage clamped to
/// [<see cref="MinZoom" />, <see cref="MaxZoom" />]; it multiplies the cell box
/// the image is scaled into, and the box is re-clipped to the viewport so a
/// zoomed image can never ask the terminal for a rect it cannot honour. Every
/// zoom step re-encodes through the block's per-rect cache, so a held-down
/// <c>+</c> re-encodes once per distinct size, not once per repeat.</para>
///
/// <para>When the terminal has no graphics protocol the viewer still opens and
/// paints the same frame (border, caption, hints) with the text card inside —
/// an honest degraded view rather than a silent no-op.</para>
/// </summary>
public sealed class ImageViewerOverlay
{
    /// <summary>Smallest viewport the viewer will paint into.</summary>
    public const int MinWidth = 20;

    /// <summary>Smallest viewport height the viewer will paint into.</summary>
    public const int MinHeight = 6;

    /// <summary>Lower zoom clamp (percent of the block's fitted size).</summary>
    public const int MinZoom = 25;

    /// <summary>Upper zoom clamp (percent of the block's fitted size).</summary>
    public const int MaxZoom = 400;

    /// <summary>Zoom applied per <c>+</c>/<c>-</c> press (percentage points).</summary>
    public const int ZoomStep = 25;

    /// <summary>Rows the caption + hint chrome costs inside the box.</summary>
    private const int ChromeRows = 4;

    private ImageBlock? _block;
    private IInlineImageSink? _sink;
    private int _zoom = 100;

    public bool Visible { get; private set; }

    /// <summary>Zoom in percent, always within [<see cref="MinZoom" />, <see cref="MaxZoom" />].</summary>
    public int Zoom => _zoom;

    /// <summary>
    ///     The block under inspection, or <see cref="Maybe{T}.None" /> while closed. The
    ///     closed state is a real state, so the public signature says so instead of
    ///     leaving every consumer to invent a null check (#592). The backing field stays
    ///     nullable — internal representation is free; the SIGNATURE is the contract.
    /// </summary>
    public Maybe<ImageBlock> Source => Maybe.From(_block);

    /// <summary>
    /// Opens the viewer over <paramref name="block"/>. Zoom resets to 100% —
    /// a fresh look at the picture, not the previous one's magnification.
    /// </summary>
    public void Show(ImageBlock block, IInlineImageSink? sink)
    {
        ArgumentNullException.ThrowIfNull(block);
        _block = block;
        _sink = sink;
        _zoom = 100;
        Visible = true;
    }

    /// <summary>Closes the viewer and drops the block reference with it.</summary>
    public void Hide()
    {
        Visible = false;
        _block = null;
        _sink = null;
        _zoom = 100;
    }

    /// <summary>Zoom in one step, clamped.</summary>
    public void ZoomIn() => SetZoom(_zoom + ZoomStep);

    /// <summary>Zoom out one step, clamped.</summary>
    public void ZoomOut() => SetZoom(_zoom - ZoomStep);

    /// <summary>
    /// Routes a decoded key. Esc / q / Enter all close (a second Enter is the
    /// "close it" gesture, mirroring how the first Enter opened it); the
    /// arrows are the mouse-free zoom equivalents of <c>+</c>/<c>-</c>.
    /// Character keys with Ctrl/Alt/Meta pass through untouched — those
    /// chords belong to the host keymap, not to a modal viewer.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Escape:
            case KeyCode.Enter:
                Hide();
                return true;
            case KeyCode.Up:
            case KeyCode.Right:
                ZoomIn();
                return true;
            case KeyCode.Down:
            case KeyCode.Left:
                ZoomOut();
                return true;
            case KeyCode.Char:
                if ((key.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
                {
                    return false;
                }

                switch (key.Character.ToString())
                {
                    case "q":
                        Hide();
                        return true;
                    case "+":
                    case "=":
                        ZoomIn();
                        return true;
                    case "-":
                    case "_":
                        ZoomOut();
                        return true;
                    default:
                        return false;
                }
            default:
                return false;
        }
    }

    private void SetZoom(int zoom) => _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>
    /// Fullscreen box: the whole viewport when it fits the minimums, default
    /// (empty) otherwise. Single source of truth for <see cref="Paint" /> and
    /// the layer seating.
    /// </summary>
    public Rect ComputeBox(Rect viewport)
    {
        if (viewport.Width < MinWidth || viewport.Height < MinHeight)
        {
            return default;
        }

        return new Rect(viewport.X, viewport.Y, viewport.Width, viewport.Height);
    }

    /// <summary>
    /// Paints the viewer inside <paramref name="viewport"/> and places the image
    /// itself through the graphics sink. No-op when hidden or too small.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!Visible || _block is not { } block)
        {
            return;
        }

        var box = ComputeBox(viewport);
        if (box.Width < MinWidth || box.Height < MinHeight)
        {
            return;
        }

        if (box.X >= buffer.Cols || box.Y >= buffer.Rows)
        {
            return;
        }

        int innerW = box.Width - 2;
        int bodyH = Math.Max(0, box.Height - ChromeRows);
        if (bodyH <= 0 || innerW <= 0)
        {
            return;
        }

        // The viewer is opaque and fullscreen: blank the whole box first so no
        // chat cell survives underneath, THEN draw the frame on top. (Filling
        // after the border would erase it.) #553: the frame itself is the
        // shared PanelChrome painter — this overlay keeps its rectilinear
        // corners (BoxStyle.SquareFrame) and owns the surface blank, which is
        // why the shared painter is told not to fill here.
        buffer.Fill(box, Cell.Blank);
        PanelChrome.PaintBorderBox(buffer, box, BoxStyle.SquareFrame);

        string header = $"{block.Name}  {block.Dimensions ?? block.MimeType}  zoom {_zoom}%";
        buffer.SetText(box.X + 1, box.Y + 1, Truncate(header, innerW),
            new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));

        var imageRect = ComputeImageRect(block, box, bodyH);
        if (!PlaceImage(block, imageRect))
        {
            PaintTextFallback(buffer, block, box, innerW);
        }

        const string Hints = "+/- zoom | esc close";
        buffer.SetText(box.X + 1, box.Bottom - 2, Truncate(Hints, innerW), ChatPalette.Dim);
    }

    /// <summary>
    /// The cell box the image is scaled into: the block's aspect-fitted size at
    /// the current zoom, centred in the body area and clipped to it. Pure and
    /// allocation-free, so the paint test can pin the geometry directly.
    /// </summary>
    public Rect ComputeImageRect(ImageBlock block, Rect box, int bodyH)
    {
        int availW = box.Width - 2;
        if (availW <= 0 || bodyH <= 0)
        {
            return default;
        }

        // The block fits itself to the same inner width the timeline used, so
        // 100% means "exactly as the feed drew it" and every zoom step scales
        // BOTH axes by the same factor — the aspect ratio never drifts. The
        // viewer has its own box, so it uses the UNGATED fit: a card-sized
        // hole in the feed must not cap how big the picture can get.
        int fitW = Math.Max(1, availW - ImageBlock.LeftPad);
        int fitRows = block.FittedGraphicHeight(fitW);
        if (fitRows <= 0)
        {
            return default;
        }

        double scale = _zoom / 100.0;
        int cols = Math.Clamp((int)Math.Round(fitW * scale), 1, availW);
        int rows = Math.Clamp((int)Math.Round(fitRows * scale), 1, bodyH);

        int x = box.X + 1 + ((availW - cols) / 2);
        int y = box.Y + 2 + ((bodyH - rows) / 2);
        return new Rect(x, y, cols, rows);
    }

    /// <summary>
    /// Hands the image to the session's graphics layer. False when the
    /// terminal has no protocol, the payload cannot be encoded, or the box is
    /// empty — the caller then paints the text card instead.
    /// </summary>
    private bool PlaceImage(ImageBlock block, Rect rect)
    {
        if (_sink is not { Enabled: true } sink || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        // Clip before encoding — see ImageBlock.TryPaintGraphic: the payload
        // names the box the terminal fills.
        Rect target = sink.ClipToFrame(rect);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return false;
        }

        byte[]? payload = block.EncodedPayload(sink, target.Width, target.Height);
        if (payload is null)
        {
            return false;
        }

        sink.Place(target, payload);
        return true;
    }

    /// <summary>Degraded body: the same two-line card the timeline shows.</summary>
    private static void PaintTextFallback(ScreenBuffer buffer, ImageBlock block, Rect box, int innerW)
    {
        var head = block.Name.AsSpan();
        buffer.SetText(box.X + 1, box.Y + 2, head[..Math.Min(innerW, head.Length)], ChatPalette.ToolOk);
        var summary = block.SummaryLine().AsSpan();
        buffer.SetText(box.X + 1, box.Y + 3, summary[..Math.Min(innerW, summary.Length)], ChatPalette.Dim);
    }

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width)];
}
