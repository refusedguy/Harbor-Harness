using System.Text;

namespace Harbor.Desktop.Abstractions.Commands;

/// <summary>
///     Turns a technical command identifier into a readable name.
///     <para>
///         Palettes and help surfaces show raw keys such as <c>MaxSteps</c>,
///         <c>CodeEditorView</c>, <c>/model</c> or <c>editor::GoToDefinition</c>
///         as row titles or labels. Splitting separators and case transitions
///         yields a name a human can read, while the raw id stays on the item for
///         dispatch and search. Pure and UI-free so any host can use it.
///     </para>
/// </summary>
public static class CommandNameHumanizer
{
    /// <summary>
    ///     Split separators and case transitions into words, then capitalize
    ///     each word. An empty or whitespace-only input yields the empty
    ///     string. The transform is idempotent: feeding the result back in
    ///     leaves it unchanged.
    /// </summary>
    public static string Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var words = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush(current, words);
                continue;
            }

            char next = i + 1 < value.Length ? value[i + 1] : '\0';
            if (current.Length > 0 && IsWordBoundary(current[^1], c, next))
            {
                Flush(current, words);
            }

            current.Append(c);
        }

        Flush(current, words);

        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];
            words[i] = $"{char.ToUpperInvariant(word[0])}{word[1..].ToLowerInvariant()}";
        }

        return string.Join(' ', words);
    }

    private static bool IsWordBoundary(char previous, char current, char next)
    {
        // lower -> upper starts a new word ("ChatView"). An acronym followed by
        // a capitalized word breaks before that word ("HTTPServer" -> HTTP Server).
        if (char.IsLower(previous) && char.IsUpper(current))
        {
            return true;
        }

        return char.IsUpper(previous) && char.IsUpper(current) && char.IsLower(next);
    }

    private static void Flush(StringBuilder current, List<string> words)
    {
        if (current.Length == 0)
        {
            return;
        }

        words.Add(current.ToString());
        current.Clear();
    }
}
