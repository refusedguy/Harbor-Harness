using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     A machine-independent snapshot of a rendered Avalonia frame — the
///     CONTENT of the visual tree, as text.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a text snapshot and not a raster.</b> The golden lane used
///         to compare SHA-256 hashes of PNG rasters, and every one of those
///         comparisons was skipped on CI: a raster is a function of the
///         machine's Skia build and font stack, so a baseline captured on one
///         host never matched another (#1795). A snapshot of the CONTENT —
///         which controls are in the tree, what they say, which colours they
///         resolved to — is a function of the code alone, so the same
///         baseline holds on every host.
///     </para>
///     <para>
///         <b>What the snapshot pins.</b> For every visible visual: its type,
///         its resolved background brush, its text, its foreground brush, its
///         font family and size, and the text of a content control. A theme
///         colour change, a changed label, a changed template, a control that
///         appeared or disappeared and a changed font size all change the
///         snapshot, and every one of them is a visible change.
///     </para>
///     <para>
///         <b>What the snapshot does not pin — and why.</b> The GEOMETRY.
///         The size of a text-bearing control is a function of the host's
///         font metrics: the app's mono slot is a SYSTEM font stack
///         ("JetBrains Mono, Cascadia Code, Fira Code, …"), so the same
///         frame measures differently on a laptop and on a runner, and a
///         baseline that carried the geometry would be pinned to one
///         machine's fonts — the exact trap the raster baseline was in.
///         The layout is therefore covered by the screenshot artifacts the
///         suite already writes (~/.harbor/test-screenshots-*) and by the
///         structural assertions in the other UI suites; the golden lane
///         covers what the raster lane could not: the CONTENT, everywhere.
///     </para>
///     <para>
///         <b>What the snapshot deliberately skips.</b> Visuals that are not
///         visible: an invisible control is not painted, so it is not part of
///         what the user sees. A control that becomes invisible still changes
///         the snapshot, because it disappears from it.
///     </para>
/// </remarks>
internal static class FrameSnapshot
{
    /// <summary>The format marker written into every snapshot.</summary>
    public const string Format = "harbor-frame-snapshot v1";

    /// <summary>
    ///     Render the visual tree of <paramref name="root" /> as a text
    ///     snapshot. The result is deterministic: the same tree always
    ///     produces the same bytes, and the bytes do not depend on the host.
    /// </summary>
    /// <param name="root">The visual to dump — the golden window.</param>
    /// <returns>The snapshot text (LF line endings, trailing newline).</returns>
    public static string Dump(Visual root)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Format).Append('\n');
        sb.Append("frame ")
          .Append(Num(root.Bounds.Width)).Append('x').Append(Num(root.Bounds.Height))
          .Append('\n');
        Append(sb, root, 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Visual visual, int depth)
    {
        // An invisible visual is not painted, and neither are its children.
        if (!visual.IsVisible)
        {
            return;
        }

        sb.Append(' ', depth * 2).Append(visual.GetType().Name);

        IBrush? background = BackgroundOf(visual);
        if (background is not null)
        {
            sb.Append(" bg=").Append(BrushOf(background));
        }

        switch (visual)
        {
            case TextBlock { } textBlock:
                sb.Append(" text=\"").Append(Escape(textBlock.Text)).Append('"');
                if (textBlock.Foreground is not null)
                {
                    sb.Append(" fg=").Append(BrushOf(textBlock.Foreground));
                }

                sb.Append(" font=\"").Append(Escape(textBlock.FontFamily.Name)).Append('"');
                sb.Append(" size=").Append(Num(textBlock.FontSize));
                break;

            case TextBox { } textBox:
                sb.Append(" text=\"").Append(Escape(textBox.Text)).Append('"');
                break;

            case ContentControl { Content: string content }:
                sb.Append(" content=\"").Append(Escape(content)).Append('"');
                break;
        }

        sb.Append('\n');

        foreach (Visual child in visual.GetVisualChildren())
        {
            Append(sb, child, depth + 1);
        }
    }

    /// <summary>
    ///     The background brush of a visual, or <c>null</c> when the visual
    ///     has no background property at all. Avalonia declares
    ///     <c>Background</c> per type rather than on <c>Control</c>, so the
    ///     lookup is a type switch over the families that can carry one.
    /// </summary>
    private static IBrush? BackgroundOf(Visual visual) => visual switch
    {
        TemplatedControl { } templated => templated.Background,
        Panel { } panel => panel.Background,
        Border { } border => border.Background,
        TextBlock { } textBlock => textBlock.Background,
        ContentPresenter { } presenter => presenter.Background,
        TextPresenter { } presenter => presenter.Background,
        _ => null,
    };

    /// <summary>
    ///     A brush as one token: the colour of a solid brush, or the type
    ///     name of anything else (a gradient, an image brush).
    /// </summary>
    private static string BrushOf(IBrush brush) => brush switch
    {
        SolidColorBrush { } solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        _ => brush.GetType().Name,
    };

    /// <summary>
    ///     A number in the invariant culture with at most two decimals, so
    ///     the snapshot never depends on the host's locale.
    /// </summary>
    private static string Num(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    ///     Escape a text so it cannot break the one-line-per-visual format.
    /// </summary>
    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
