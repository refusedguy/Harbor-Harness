namespace Harbor.Tui.AnsiPlain;

using Harbor.Abstractions.Tui;

/// <summary>
///     Renders a <see cref="Uri"/> as a QR code using Unicode half-blocks
///     (█ ▀ ▄) — no GDI, no System.Drawing, no external packages.
/// </summary>
/// <remarks>
///     The module matrix comes from the shared <see cref="QrMatrix"/> encoder
///     (issue #192); only the half-block painting stays terminal-specific.
/// </remarks>
public static class TerminalQrRenderer
{
    public static string Render(Uri uri)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        return Render(uri.ToString());
    }

    private static string Render(string text)
    {
        if (text.Length == 0) return string.Empty;
        return RenderMatrix(QrMatrix.Encode(text));
    }

    private static string RenderMatrix(bool[,] m)
    {
        int count = m.GetLength(0);
        var sb = new System.Text.StringBuilder();
        int height = (count + 1) / 2;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < count; x++)
            {
                bool top = m[x, y * 2];
                bool bottom = y * 2 + 1 < count && m[x, y * 2 + 1];

                sb.Append(top && bottom ? '█' : top ? '▀' : bottom ? '▄' : ' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
