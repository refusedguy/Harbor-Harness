namespace Harbor.Tui.Ansi;

public static class TerminalQrRenderer
{
    public static string Render(Uri uri)
    {
        return $"QR: {uri}\n(TODO: implement Unicode half-block QR generator)";
    }
}
