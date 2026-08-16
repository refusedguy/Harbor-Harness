namespace Harbor.Desktop.Abstractions.DesignSystem;
/// <summary>
///     Font-family constants. Platforms resolve these via font fallback chains
///     (e.g. Avalonia picks the first available font from the comma-separated
///     list). Keep these strings in sync with the platform <c>Typography.axaml</c>
///     / <c>Typography.xaml</c> / Blazor CSS files.
/// </summary>
public static class Typography
{
    /// <summary>
    ///     UI font stack — Inter is the primary; Segoe UI (Windows), SF Pro
    ///     (macOS), and Roboto (Linux/Android) provide fallbacks.
    /// </summary>
    public const string UiFontStack = "Inter, Segoe UI, San Francisco, Roboto, Arial";

    /// <summary>
    ///     Code font stack — JetBrains Mono is the primary; Cascadia Code,
    ///     Fira Code, Menlo, Consolas, monospace provide per-OS fallbacks.
    /// </summary>
    public const string CodeFontStack = "JetBrains Mono, Cascadia Code, Fira Code, Menlo, Consolas, monospace";
}
