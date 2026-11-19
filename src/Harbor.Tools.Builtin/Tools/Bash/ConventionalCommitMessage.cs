// ConventionalCommitMessage.cs: #2680 F141. Pure primitive for Conventional
// Commits messages: parse a generated message into its components and render
// the canonical form back out. It starts no process, calls no LLM, reads no
// repository and changes no product path. It is the parse/format half of
// commit-message generation, the part that runs on whatever a generator
// proposes before that text reaches git.
using System.Diagnostics.CodeAnalysis;

namespace Harbor.Tools.Builtin;

/// <summary>
///     A parsed Conventional Commits 1.0.0 message (#2680 F141). Immutable,
///     stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         The header grammar is <c>type(scope)!: description</c>. The
///         <c>scope</c> and the <c>!</c> breaking marker are optional. The
///         description is required. A body, when present, is separated from
///         the header by a blank line.
///     </para>
///     <para>
///         <see cref="Parse" /> accepts a fenced or decorated generator reply
///         and returns <see langword="null" /> for anything that does not
///         match the grammar. All comparisons are ordinal and never
///         culture-sensitive.
///     </para>
/// </remarks>
public sealed record ConventionalCommitMessage
{
    private static readonly string[] FenceMarkers = ["```", "~~~"];

    private static readonly string[] BreakingFooters = ["BREAKING CHANGE:", "BREAKING-CHANGE:"];

    /// <summary>The recommended Conventional Commits types.</summary>
    public static IReadOnlyList<string> KnownTypes { get; } =
    [
        "feat", "fix", "build", "chore", "ci", "docs",
        "style", "refactor", "perf", "test", "revert"
    ];

    /// <summary>The commit type, for example <c>feat</c>.</summary>
    public string Type { get; }

    /// <summary>The optional scope, or <see langword="null" /> when absent.</summary>
    public string? Scope { get; }

    /// <summary>Whether the message declares a breaking change.</summary>
    public bool Breaking { get; }

    /// <summary>The short description from the header.</summary>
    public string Description { get; }

    /// <summary>The optional body, or <see langword="null" /> when absent.</summary>
    public string? Body { get; }

    private ConventionalCommitMessage(string type, string? scope, bool breaking, string description, string? body)
    {
        Type = type;
        Scope = scope;
        Breaking = breaking;
        Description = description;
        Body = body;
    }

    /// <summary>
    ///     Parses <paramref name="message" /> into its components, or returns
    ///     <see langword="null" /> when the text is not a Conventional Commit.
    /// </summary>
    /// <param name="message">A generated commit message, possibly fenced.</param>
    public static ConventionalCommitMessage? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        string text = message.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');

        int first = 0;
        int last = lines.Length;
        SkipBlankLines(lines, ref first, last);
        while (last > first && string.IsNullOrWhiteSpace(lines[last - 1])) last--;
        if (first < last && IsFence(lines[first]))
        {
            first++;
            SkipBlankLines(lines, ref first, last);
        }
        if (last > first && IsFence(lines[last - 1])) last--;
        if (first >= last) return null;

        string header = lines[first].Trim();
        if (!TryParseHeader(header, out string type, out string? scope, out bool breaking, out string description))
            return null;

        int bodyStart = first + 1;
        SkipBlankLines(lines, ref bodyStart, last);
        int bodyEnd = last;
        while (bodyEnd > bodyStart && string.IsNullOrWhiteSpace(lines[bodyEnd - 1])) bodyEnd--;

        string? body = null;
        if (bodyEnd > bodyStart)
        {
            body = string.Join('\n', lines, bodyStart, bodyEnd - bodyStart);
            if (HasBreakingFooter(body)) breaking = true;
        }

        return new ConventionalCommitMessage(type, scope, breaking, description, body);
    }

    /// <summary>
    ///     Parses <paramref name="message" /> without throwing.
    ///     <paramref name="result" /> is non-null exactly when the method returns
    ///     <see langword="true" />.
    /// </summary>
    /// <param name="message">A generated commit message, possibly fenced.</param>
    /// <param name="result">The parsed message, or <see langword="null" /> on failure.</param>
    public static bool TryParse(string? message, [NotNullWhen(true)] out ConventionalCommitMessage? result)
    {
        result = Parse(message);
        return result is not null;
    }

    /// <summary>
    ///     Builds a message from validated parts, or returns
    ///     <see langword="null" /> when the parts do not form a valid header.
    /// </summary>
    /// <param name="type">The commit type.</param>
    /// <param name="scope">An optional scope.</param>
    /// <param name="breaking">Whether the change is breaking.</param>
    /// <param name="description">The short description.</param>
    /// <param name="body">An optional body.</param>
    public static ConventionalCommitMessage? Create(
        string? type, string? scope, bool breaking, string? description, string? body = null)
    {
        if (!IsType(type)) return null;
        if (scope is not null && !IsScope(scope)) return null;
        if (string.IsNullOrWhiteSpace(description)) return null;

        string? trimmedBody = string.IsNullOrWhiteSpace(body) ? null : body!.Trim();
        return new ConventionalCommitMessage(type!, scope, breaking, description!.Trim(), trimmedBody);
    }

    /// <summary>Whether <paramref name="type" /> is one of <see cref="KnownTypes" />.</summary>
    /// <param name="type">The type to check.</param>
    public static bool IsKnownType(string? type)
    {
        if (type is null) return false;
        foreach (string known in KnownTypes)
            if (string.Equals(type, known, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The canonical <c>type(scope)!: description</c> header.</summary>
    public string Header => Breaking
        ? $"{Type}{(Scope is null ? string.Empty : $"({Scope})")}!: {Description}"
        : $"{Type}{(Scope is null ? string.Empty : $"({Scope})")}: {Description}";

    /// <summary>The header, followed by the body when one is present.</summary>
    public override string ToString() => Body is null ? Header : $"{Header}\n\n{Body}";

    private static void SkipBlankLines(string[] lines, ref int index, int limit)
    {
        while (index < limit && string.IsNullOrWhiteSpace(lines[index])) index++;
    }

    private static bool IsFence(string line)
    {
        string trimmed = line.Trim();
        foreach (string marker in FenceMarkers)
            if (trimmed.StartsWith(marker, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool TryParseHeader(
        string header,
        out string type,
        out string? scope,
        out bool breaking,
        out string description)
    {
        type = string.Empty;
        scope = null;
        breaking = false;
        description = string.Empty;

        int colon = header.IndexOf(':');
        if (colon <= 0) return false;

        description = header[(colon + 1)..].Trim();
        if (description.Length == 0) return false;

        string head = header[..colon].Trim();
        if (head.EndsWith('!'))
        {
            breaking = true;
            head = head[..^1].TrimEnd();
        }
        if (head.Length == 0) return false;

        int open = head.IndexOf('(');
        if (open >= 0)
        {
            int close = head.IndexOf(')', open + 1);
            if (close != head.Length - 1) return false;

            type = head[..open].Trim();
            scope = head[(open + 1)..close].Trim();
            if (!IsScope(scope)) return false;
        }
        else
        {
            type = head;
        }

        return IsType(type);
    }

    private static bool HasBreakingFooter(string body)
    {
        string[] lines = body.Replace("\r\n", "\n").Split('\n');
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            foreach (string footer in BreakingFooters)
                if (trimmed.StartsWith(footer, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        foreach (char c in type)
            if (c < 'a' || c > 'z') return false;
        return true;
    }

    private static bool IsScope(string? scope)
    {
        if (string.IsNullOrEmpty(scope)) return false;
        foreach (char c in scope)
        {
            bool allowed = (c >= 'a' && c <= 'z')
                           || (c >= '0' && c <= '9')
                           || c is '.' or '_' or '-' or '/';
            if (!allowed) return false;
        }
        return true;
    }
}
