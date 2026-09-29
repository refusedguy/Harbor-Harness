namespace Harbor.Tui.SpectreTui.View;
/// <summary>Shared escape / truncate / status styling. No layout, no history.</summary>
internal static class ChatMarkup
{
    public static string Escape(string? text)
        => (text ?? string.Empty)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    public static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return max <= 1 ? text[..1] : text[..(max - 1)] + "…";
    }

    public static string StatusPill(string? status) => (status ?? "idle").ToLowerInvariant() switch
    {
        "running" => "[black on green] RUN [/]",
        "compacting" => "[black on yellow] COMPACT [/]",
        "error" => "[white on red] ERR [/]",
        "idle" => "[grey]idle[/]",
        _ => $"[grey]{Escape(status)}[/]"
    };
}
