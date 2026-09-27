using System.Collections.Frozen;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Fence-tag → <see cref="ILanguageSupport"/> registry (issue #198). Replaces
/// the old <c>ClassifyLanguage</c> if-ladder: adding a language means adding
/// its support class and listing it here (or constructing a custom registry),
/// the tokenizer itself is never touched (OCP).
/// </summary>
public sealed class LanguageSupportRegistry
{
    /// <summary>Default registry with the six builtin language groups.</summary>
    public static readonly LanguageSupportRegistry Default = new(
    [
        CSharpLanguageSupport.Instance,
        JsLanguageSupport.Instance,
        PythonLanguageSupport.Instance,
        GoLanguageSupport.Instance,
        RustLanguageSupport.Instance,
        SqlLanguageSupport.Instance,
    ]);

    private readonly FrozenDictionary<string, ILanguageSupport> _byTag;

    /// <summary>
    /// Builds a registry from language supports. Both the canonical
    /// <see cref="ILanguageSupport.LanguageId"/> and every
    /// <see cref="ILanguageSupport.Aliases"/> entry resolve (case-insensitive,
    /// like the old classifier).
    /// </summary>
    public LanguageSupportRegistry(IEnumerable<ILanguageSupport> languages)
    {
        var byTag = new Dictionary<string, ILanguageSupport>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
        {
            byTag[language.LanguageId] = language;
            foreach (var alias in language.Aliases)
            {
                byTag[alias] = language;
            }
        }

        _byTag = byTag.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a fence language tag (e.g. <c>"cs"</c>, <c>"typescript"</c>).
    /// Returns <c>null</c> for null/blank/unknown tags — the tokenizer then
    /// emits plain spans (same as the old <c>Lang.None</c> path).
    /// </summary>
    public ILanguageSupport? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return Resolve(language.AsSpan());
    }

    /// <summary>Span-based resolve (trims the tag, no intermediate string when blank).</summary>
    public ILanguageSupport? Resolve(ReadOnlySpan<char> language)
    {
        var tag = language.Trim();
        if (tag.IsEmpty)
        {
            return null;
        }

        // One alloc per resolve (call sites are per-region/per-line, never
        // per-token): FrozenDictionary<string, …> has no span lookup.
        return _byTag.TryGetValue(tag.ToString(), out var support) ? support : null;
    }
}
