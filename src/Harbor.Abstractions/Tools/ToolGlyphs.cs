namespace Harbor.Abstractions.Tools;

/// <summary>
///     The glyph a tool shows next to its name in every UI surface (#680).
/// </summary>
/// <remarks>
///     <para>
///         The glyph used to live in three hand-written tables inside the UI layer
///         — one per rendering path — and adding a tool meant editing them. Two of
///         the three disagreed with each other, and all three keyed the web-fetch
///         tool as <c>"web_fetch"</c> while the tool is <c>"webfetch"</c>, so that
///         arm was dead. A glyph is a property of the tool, so it is declared next
///         to <see cref="ITool.DisplayName" /> and travels with the tool call.
///     </para>
///     <para>
///         A plugin that ships its own tool declares its own glyph and no existing
///         C# changes — the same property #560 gave providers by moving their
///         picker icon into <c>providers/&lt;id&gt;.json</c>.
///     </para>
/// </remarks>
public static class ToolGlyphs
{
    /// <summary>
    ///     Rendered for a tool that declares no glyph of its own. A generic
    ///     wrench is visibly unspecific rather than wrong, which is the direction a
    ///     default has to fail in (docs/PATTERNS.md, "Default interface members").
    /// </summary>
    public const string Default = "🔧";
}
