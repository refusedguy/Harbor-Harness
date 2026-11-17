using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     Seats the floating terminal pane (issue #1491) on the existing
///     <see cref="OverlayStack"/> z-plane and <c>OverlayLayerBase</c> skeleton.
///     Modal and opaque while attached: it occupies a floating box (not a
///     fullscreen takeover, unlike the diff/image viewers) and captures input
///     through the host's hand-written cascade in <c>ReplInputLoop</c>. Keys are
///     deliberately NOT routed through <see cref="OverlayStack.RouteKey"/> — no
///     product host enters that route (#812/#858); the host reaches the same
///     controller directly, exactly as it does for the image viewer.
/// </summary>
public sealed class FloatingTerminalOverlayLayer : OverlayLayerBase<FloatingTerminalController>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "floating-terminal";

    public FloatingTerminalOverlayLayer(FloatingTerminalController controller)
        : base(controller, LayerId, opaque: true, hitTransparent: false, isModal: true)
    {
    }

    /// <summary>The floating box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => FloatingTerminalPanePainter.ComputeBox(viewport);

    /// <summary>Shown only while a live session is attached.</summary>
    protected override bool IsShownCore =>
        Overlay.IsAttached && Overlay.Session is { IsAlive: true };

    protected override void PaintCore(ScreenBuffer buffer, Rect box) =>
        FloatingTerminalPanePainter.Paint(buffer, box, Overlay.Session!);
}

/// <summary>
///     Pure paint + geometry for the floating pane. Kept separate from the seat
///     so both are exercisable in a headless buffer test without a host.
/// </summary>
internal static class FloatingTerminalPanePainter
{
    internal const int MinWidth = 24;
    internal const int MinHeight = 7;

    private const string Header = " Floating terminal  [ctrl+x t detach] ";

    /// <summary>
    ///     A centred floating box at ~75%×70% of the viewport, clamped to a
    ///     minimum. Oversized requests and tiny viewports yield an empty rect
    ///     so the layer hides rather than painting a clipped skeleton.
    /// </summary>
    internal static Rect ComputeBox(Rect viewport)
    {
        if (viewport.Width < MinWidth || viewport.Height < MinHeight)
        {
            return default;
        }

        int w = Math.Max(MinWidth, Math.Min(viewport.Width - 4, (viewport.Width * 3) / 4));
        int h = Math.Max(MinHeight, Math.Min(viewport.Height - 4, (viewport.Height * 7) / 10));
        int x = viewport.X + ((viewport.Width - w) / 2);
        int y = viewport.Y + ((viewport.Height - h) / 2);
        return new Rect(x, y, w, h);
    }

    /// <summary>Paints a bordered box and the newest scrollback lines.</summary>
    internal static void Paint(ScreenBuffer buffer, Rect box, FloatingTerminalSession session)
    {
        // Single owner of the dialog frame (#553): PanelChrome blanks the
        // interior and draws the rounded box. Spelling corners here would
        // create a second box painter (PanelChromeTests).
        PanelChrome.PaintBorderBox(buffer, box, BoxStyle.RoundedPanel);

        int innerW = Math.Max(0, box.Width - 2);
        if (innerW > 0)
        {
            string header = Header.Length <= innerW ? Header : Header[..innerW];
            buffer.SetText(box.X + 1, box.Y, header, CellStyle.Plain);
        }

        // Newest lines at the bottom, like a terminal viewport.
        IReadOnlyList<string> lines = session.Lines;
        int bodyTop = box.Y + 1;
        int bodyRows = Math.Max(0, box.Height - 2);
        for (int row = 0; row < bodyRows; row++)
        {
            int index = lines.Count - bodyRows + row;
            if (index < 0 || index >= lines.Count)
            {
                continue;
            }

            string line = lines[index];
            if (line.Length > innerW)
            {
                line = line[^innerW..];
            }

            buffer.SetText(box.X + 1, bodyTop + row, line, CellStyle.Plain);
        }
    }
}
