// BranchNameGenerator.cs: #2680 F115. Turns a proposed branch name into a
// valid kebab-case git branch slug. The helper is a pure function over a string:
// it starts no process, calls no LLM, reads no repository and changes no
// product path. It is the parse half of the branch-name feature, the part that
// runs on whatever the generator proposes before that name reaches git.
using System.Text;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Parses a proposed branch name into a kebab-case git branch slug
///     (#2680 F115). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         The input is the text a name generator produced, not a git ref. A
///         Markdown code fence is skipped, a leading quote or backtick pair and
///         a <c>Branch name:</c>-style prefix are removed, and the first
///         meaningful line wins.
///     </para>
///     <para>
///         Every run of non-ASCII-alphanumeric characters becomes one hyphen,
///         so <c>Add Login Flow</c>, <c>add_login_flow</c> and
///         <c>feature/add-login</c> all yield a flat kebab-case slug. ASCII
///         letters are lower-cased; non-ASCII letters are treated as
///         separators. Matching is ordinal, never culture-sensitive.
///     </para>
///     <para>
///         A slug longer than <see cref="MaxLength" /> is cut at the last
///         hyphen inside the limit, so a word is never split. The reserved git
///         name <c>HEAD</c> is rejected.
///     </para>
/// </remarks>
public static class BranchNameGenerator
{
    /// <summary>Maximum length of a generated slug.</summary>
    public const int MaxLength = 64;

    /// <summary>Prefixes a name generator may put in front of the slug.</summary>
    private static readonly string[] Prefixes = ["branch name:", "branch:", "name:"];

    /// <summary>
    ///     Returns the kebab-case branch slug described by
    ///     <paramref name="response" />, or <see langword="null" /> when the
    ///     response contains no usable name.
    /// </summary>
    /// <param name="response">Proposed name, possibly decorated by a generator.</param>
    public static string? Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        string line = FirstCandidateLine(response);
        if (line.Length == 0) return null;

        string candidate = StripPrefix(StripDecoration(line));
        string slug = ToSlug(candidate);
        if (slug.Length == 0) return null;

        slug = Cap(slug);
        if (slug.Length == 0) return null;
        if (string.Equals(slug, "head", StringComparison.OrdinalIgnoreCase)) return null;

        return slug;
    }

    /// <summary>
    ///     Attempts to parse a branch slug, writing <see cref="string.Empty" />
    ///     to <paramref name="branchName" /> on failure.
    /// </summary>
    /// <param name="response">Proposed name, possibly decorated by a generator.</param>
    /// <param name="branchName">The parsed slug, or an empty string.</param>
    /// <returns><see langword="true" /> when a slug was produced.</returns>
    public static bool TryParse(string? response, out string branchName)
    {
        string? parsed = Parse(response);
        branchName = parsed ?? string.Empty;
        return parsed is not null;
    }

    private static string FirstCandidateLine(string response)
    {
        string[] lines = response.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.StartsWith("~~~", StringComparison.Ordinal)) continue;
            return line;
        }

        return string.Empty;
    }

    private static string StripDecoration(string line)
    {
        string value = line.Trim();
        while (value.Length >= 2)
        {
            char first = value[0];
            char last = value[^1];
            bool paired = (first == '`' && last == '`') ||
                          (first == '\'' && last == '\'') ||
                          (first == '"' && last == '"');
            if (!paired) break;
            value = value[1..^1].Trim();
        }

        return value;
    }

    private static string StripPrefix(string line)
    {
        for (int i = 0; i < Prefixes.Length; i++)
        {
            string label = Prefixes[i];
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                return line[label.Length..].Trim();
        }

        // A generator may write a sentence instead of a bare prefix. Use the
        // text after a colon only when the left-hand side talks about a name.
        int colon = line.IndexOf(':');
        if (colon > 0 && colon < 48)
        {
            string left = line[..colon];
            if (left.Contains("branch", StringComparison.OrdinalIgnoreCase) ||
                left.Contains("name", StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return line;
    }

    private static string ToSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool separatorPending = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (IsAsciiLetterOrDigit(c))
            {
                if (separatorPending && builder.Length > 0) builder.Append('-');
                builder.Append(ToLowerAscii(c));
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string Cap(string slug)
    {
        if (slug.Length <= MaxLength) return slug;

        int cut = slug.LastIndexOf('-', MaxLength - 1);
        if (cut <= 0) cut = MaxLength;
        return slug[..cut].TrimEnd('-');
    }

    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') ||
        (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9');

    private static char ToLowerAscii(char c) => c >= 'A' && c <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
