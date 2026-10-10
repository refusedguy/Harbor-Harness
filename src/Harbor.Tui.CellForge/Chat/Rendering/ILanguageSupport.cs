using System.Collections.Frozen;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Keyword vocabulary for one syntax-highlighted language group (issue #198).
/// New languages are added by implementing this interface (new file, no edits
/// to the tokenizer) and registering the instance in
/// <see cref="LanguageSupportRegistry"/> — the old
/// <c>ClassifyLanguage</c> + <c>IsKeyword</c> switch ladder is gone.
/// </summary>
public interface ILanguageSupport
{
    /// <summary>Canonical id used in fence tags (e.g. <c>"csharp"</c>).</summary>
    string LanguageId { get; }

    /// <summary>
    /// Extra fence tags resolving to this language (e.g. <c>"cs"</c>,
    /// <c>"c#"</c>). Matched case-insensitively by the registry.
    /// </summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// Keyword vocabulary. Ordinal, case-sensitive — mirrors the Avalonia
    /// source 1:1, including the SQL upper/lower pair quirk (so
    /// <c>"Select"</c> stays plain there too).
    /// </summary>
    IReadOnlySet<string> Keywords { get; }

    /// <summary>Ordinal keyword check over an identifier slice.</summary>
    bool IsKeyword(ReadOnlySpan<char> word);
}

/// <summary>
/// Base <see cref="ILanguageSupport"/> over a single
/// <see cref="FrozenSet{T}"/> vocabulary (AOT-clean, BCL-only). Subclasses
/// only declare their id, aliases, and keyword array.
/// </summary>
public abstract class LanguageSupportBase : ILanguageSupport
{
    private readonly FrozenSet<string> _keywords;
    private readonly string[] _aliases;

    /// <inheritdoc />
    public abstract string LanguageId { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases => _aliases;

    /// <inheritdoc />
    public IReadOnlySet<string> Keywords => _keywords;

    /// <summary>
    /// Builds the support from its alias and keyword tables. Both tables are
    /// copied into frozen collections once (per language singleton).
    /// </summary>
    protected LanguageSupportBase(string[] aliases, string[] keywords)
    {
        _aliases = aliases;
        _keywords = keywords.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsKeyword(ReadOnlySpan<char> word) => _keywords.Contains(word.ToString());
}
