using System.Text;

namespace Harbor.Desktop.ViewModels;

/// <summary>
///     #2682 Z09: turns a technical command identifier into a readable name.
///     <para>
///         The palette's built-in rows carry keys such as <c>CodeEditorView</c>
///         and slash commands are declared as <c>/model</c>. Neither reads well
///         in a result row, and a plugin-supplied command may be worse still
///         (<c>editor::GoToDefinition</c>). This helper is pure and UI-free so
///         the mapping can be pinned without standing up a view.
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
