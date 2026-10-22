// SourceParse.cs — the parsing substrate the architecture suite's text rules
// read the tree through.
//
// WHY THIS FILE EXISTS
// --------------------
// Every source-scanning rule in this suite grades TEXT, and the text came
// from three places, each with its own cost:
//
//   1. `SourceScan.StripComments` — the comment stripper. It is NOT here: it
//      is `SourceCommentBlanker`, and it was rewritten on its own (#2167),
//      which proved its equivalence over the live tree in
//      `CommentStripperEquivalenceTests`. A second implementation of the
//      same stripper would be a second answer to the same question, and two
//      answers drift.
//   2. `DeadPublicInterfaceProbe.BlankLiteralsAndComments` — a correct lexer,
//      but one that appends to a `StringBuilder` one character at a time.
//   3. `Regex.Matches` over a whole file to count identifiers — a `Match` and
//      a `string` per identifier, for every identifier in the tree.
//
// This file replaces 2 and 3 with one span-based pass per file.
//
// THE CONTRACT: THE SAME BYTES, AND THE SAME VERDICTS
// -----------------------------------------------------
// A parser that grades a tree has one duty above all: grade the SAME tree.
// Every method here therefore has a `...Legacy` twin — the OLD implementation,
// kept — and `SourceParseParityTests` asserts that the two agree BYTE FOR BYTE
// over every file of the repository. That test is what makes the rewrite safe:
// a difference in the output is a difference in some rule's verdict, and the
// test fails before the rule does.
//
// The legacy twins are not dead code to be cleaned up later: they are the
// MEASUREMENT's "before" column. The rewrite is only worth anything if the
// "before" is still there to be compared against.
//
// THE MODERN APIs, AND WHERE EACH ONE EARNS ITS PLACE
// ----------------------------------------------------
//   * `ReadOnlySpan<char>` — every method takes a span and allocates nothing
//     for the scan itself.
//   * `ArrayPool<char>` — the blanked copy of a file is rented, not allocated.
//   * `SearchValues<char>` + `IndexOfAny` — the literal lexer asks for the
//     next character that can START a comment or a literal, in one
//     vectorised step, instead of testing every character against five
//     branches.
//   * `Regex.EnumerateMatches` — the identifier counter walks the matches
//     WITHOUT a `Match` object and WITHOUT a `string` per match: the engine
//     hands back a `ValueMatch` struct, and the token is looked up in the
//     dictionary through `GetAlternateLookup<ReadOnlySpan<char>>()`, so a
//     string is materialised only for a token the dictionary has not seen.
//
// WHAT IS NOT HERE, AND WHY
// --------------------------
//   * A ROSLYN PARSER for the rules. `CSharpSyntaxTree.ParseText` is the
//     textbook way to find a comment, and `SourceParse.StripCommentsRoslyn`
//     implements it — and the measurement says it is the SLOWEST of the
//     variants by an order of magnitude, because it parses the whole file to
//     find what a single pass over the characters finds. It is kept, measured,
//     and rejected: that is the point of having measured it.
//   * A SEMANTIC PARSER for the rules that grade SHAPES. The Roslyn harness
//     (`Roslyn/RoslynProjectCompilation.cs`) already builds one compilation
//     per product project, and the analyzer family grades shapes through it.
//     The TEXT rules stay text rules: a rule about a line's shape is
//     cheaper to answer with a line's shape.

using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The parsing substrate every source-scanning rule in the architecture
///     suite reads through: the literal-blanking lexer and the identifier
///     counter.
/// </summary>
internal static class SourceParse
{
    /// <summary>
    ///     The characters a C# lexer has to look at: the two comment openers,
    ///     the two literal openers and the verbatim-string sigil. Everything
    ///     else in a source file is ordinary code, and the lexer skips it in
    ///     one vectorised step.
    /// </summary>
    private static readonly SearchValues<char> LexicalOpeners = SearchValues.Create("/\"'@");

    /// <summary>Matches an identifier: a letter or underscore, then word characters.</summary>
    private static readonly Regex IdentifierPattern = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    // =====================================================================
    // 1. THE ROSLYN STRIPPER — the modern variant, measured and rejected.
    // =====================================================================

    /// <summary>
    ///     Blanks out the comments of a source file by PARSING it with Roslyn
    ///     and asking the syntax tree where its comments are.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the textbook way to find a comment, and it is the
    ///         variant the measurement REJECTS: parsing a file to find what a
    ///         single pass over its characters finds costs an order of
    ///         magnitude more, and the architecture suite's rules read the
    ///         whole tree.
    ///     </para>
    ///     <para>
    ///         It is kept because it is RIGHT where the text stripper is
    ///         wrong: the text stripper cannot tell a comment from a
    ///         <c>//</c> inside a string literal, so it eats the rest of a
    ///         line that holds a URL. This variant sees the difference, and
    ///         the measurement reports every line where the two disagree.
    ///     </para>
    /// </remarks>
    /// <param name="source">The raw source.</param>
    /// <returns>The source with its comments blanked out.</returns>
    internal static string StripCommentsRoslyn(string source)
    {
        int n = source.Length;
        if (n == 0)
        {
            return string.Empty;
        }

        char[] rented = ArrayPool<char>.Shared.Rent(n);
        try
        {
            source.AsSpan().CopyTo(rented);
            Span<char> buffer = rented.AsSpan(0, n);

            SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
            foreach (SyntaxTrivia trivia in tree.GetRoot().DescendantTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    continue;
                }

                TextSpan span = trivia.Span;
                int end = Math.Min(span.End, n);
                for (int k = span.Start; k < end; k++)
                {
                    if (buffer[k] != '\n')
                    {
                        buffer[k] = ' ';
                    }
                }
            }

            return new string(rented, 0, n);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    // =====================================================================
    // 2. THE LITERAL BLANKER.
    // =====================================================================

    /// <summary>
    ///     Blanks out the comments AND the string and char literal CONTENTS of
    ///     a source file, preserving the line structure and the length.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The comment stripper keeps the literals, which is right for a
    ///         rule about a rendered string and wrong for a rule that COUNTS
    ///         BRACES to find a type's body: a <c>"}"</c> inside a literal
    ///         throws the depth off and hides a declaration, which reads as
    ///         "no members" and reports a live contract as dead.
    ///     </para>
    ///     <para>
    ///         The output is BYTE FOR BYTE the output of
    ///         <see cref="BlankLiteralsAndCommentsLegacy" />, and the parity
    ///         test compares the two over every file of the repository.
    ///     </para>
    /// </remarks>
    /// <param name="source">The raw source.</param>
    /// <returns>The source with its comments and literal contents blanked out.</returns>
    internal static string BlankLiteralsAndComments(string source)
    {
        int n = source.Length;
        if (n == 0)
        {
            return string.Empty;
        }

        char[] rented = ArrayPool<char>.Shared.Rent(n);
        try
        {
            source.AsSpan().CopyTo(rented);
            BlankLiteralsAndCommentsInPlace(rented.AsSpan(0, n));
            return new string(rented, 0, n);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     The lexer, writing into a buffer it already owns.
    /// </summary>
    /// <param name="text">The source, as a writable buffer.</param>
    /// <remarks>
    ///     The legacy form appended to a <see cref="StringBuilder" />, one
    ///     character at a time, and the string it returned was exactly as long
    ///     as the input — which is what lets this form write IN PLACE.
    /// </remarks>
    private static void BlankLiteralsAndCommentsInPlace(Span<char> text)
    {
        int n = text.Length;
        int i = 0;

        while (i < n)
        {
            // Skip the ordinary code in one vectorised step: the next
            // character that can start a comment or a literal.
            int next = text.Slice(i).IndexOfAny(LexicalOpeners);
            if (next < 0)
            {
                return;
            }

            i += next;
            char c = text[i];
            char following = i + 1 < n ? text[i + 1] : '\0';

            if (c == '/' && following == '/')
            {
                while (i < n && text[i] != '\n')
                {
                    text[i] = ' ';
                    i++;
                }

                continue;
            }

            if (c == '/' && following == '*')
            {
                text[i] = ' ';
                text[i + 1] = ' ';
                i += 2;

                while (i < n)
                {
                    if (text[i] == '*' && i + 1 < n && text[i + 1] == '/')
                    {
                        text[i] = ' ';
                        text[i + 1] = ' ';
                        i += 2;
                        break;
                    }

                    if (text[i] != '\n')
                    {
                        text[i] = ' ';
                    }

                    i++;
                }

                continue;
            }

            if (c == '@' && following == '"')
            {
                text[i] = ' ';
                text[i + 1] = ' ';
                i += 2;

                while (i < n)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"')
                        {
                            text[i] = ' ';
                            text[i + 1] = ' ';
                            i += 2;
                            continue;
                        }

                        text[i] = ' ';
                        i++;
                        break;
                    }

                    if (text[i] != '\n')
                    {
                        text[i] = ' ';
                    }

                    i++;
                }

                continue;
            }

            if (c == '"' && following == '"' && i + 2 < n && text[i + 2] == '"')
            {
                text[i] = ' ';
                text[i + 1] = ' ';
                text[i + 2] = ' ';
                i += 3;

                while (i < n)
                {
                    if (text[i] == '"' && i + 2 < n && text[i + 1] == '"' && text[i + 2] == '"')
                    {
                        text[i] = ' ';
                        text[i + 1] = ' ';
                        text[i + 2] = ' ';
                        i += 3;
                        break;
                    }

                    if (text[i] != '\n')
                    {
                        text[i] = ' ';
                    }

                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                text[i] = ' ';
                i++;

                while (i < n)
                {
                    if (text[i] == '\\' && i + 1 < n)
                    {
                        text[i] = ' ';
                        text[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        text[i] = ' ';
                        i++;
                        break;
                    }

                    if (text[i] != '\n')
                    {
                        text[i] = ' ';
                    }

                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                text[i] = ' ';
                i++;

                while (i < n)
                {
                    if (text[i] == '\\' && i + 1 < n)
                    {
                        text[i] = ' ';
                        text[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    if (text[i] == '\'')
                    {
                        text[i] = ' ';
                        i++;
                        break;
                    }

                    if (text[i] != '\n')
                    {
                        text[i] = ' ';
                    }

                    i++;
                }

                continue;
            }

            // A sigil that is not a verbatim string's: ordinary code.
            i++;
        }
    }

    /// <summary>
    ///     The literal blanker as it was before the rewrite: the same lexer,
    ///     appending to a <see cref="StringBuilder" />.
    /// </summary>
    /// <remarks>
    ///     Kept as the measurement's "before" column, and as the parity
    ///     test's reference. Do not use this in a rule.
    /// </remarks>
    /// <param name="source">The raw source.</param>
    /// <returns>The source with its comments and literal contents blanked out.</returns>
    internal static string BlankLiteralsAndCommentsLegacy(string source)
    {
        var output = new StringBuilder(source.Length);
        int i = 0;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];
            char next = i + 1 < n ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < n && source[i] != '\n')
                {
                    output.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                output.Append("  ");
                i += 2;

                while (i < n)
                {
                    if (source[i] == '*' && i + 1 < n && source[i + 1] == '/')
                    {
                        output.Append("  ");
                        i += 2;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '@' && next == '"')
            {
                output.Append("  ");
                i += 2;

                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < n && source[i + 1] == '"')
                        {
                            output.Append("  ");
                            i += 2;
                            continue;
                        }

                        output.Append(' ');
                        i++;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '"' && next == '"' && i + 2 < n && source[i + 2] == '"')
            {
                output.Append("   ");
                i += 3;

                while (i < n)
                {
                    if (source[i] == '"' && i + 2 < n && source[i + 1] == '"' && source[i + 2] == '"')
                    {
                        output.Append("   ");
                        i += 3;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                output.Append(' ');
                i++;

                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        output.Append("  ");
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        output.Append(' ');
                        i++;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                output.Append(' ');
                i++;

                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        output.Append("  ");
                        i += 2;
                        continue;
                    }

                    if (source[i] == '\'')
                    {
                        output.Append(' ');
                        i++;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    // =====================================================================
    // 3. THE IDENTIFIER COUNTER.
    // =====================================================================

    /// <summary>
    ///     Counts the identifiers of a source file into a dictionary, adding to
    ///     the counts that are already there.
    /// </summary>
    /// <remarks>
    ///     The dictionary is looked up through
    ///     <see cref="Dictionary{TKey, TValue}.GetAlternateLookup{TAlternateKey}" />,
    ///     so a token is looked up as a SPAN and a string is materialised only
    ///     for a token the dictionary has not seen. The legacy form allocated
    ///     a <see cref="Match" /> and a <see cref="string" /> for EVERY
    ///     identifier of EVERY file, and the tree has millions of them.
    ///     <para>
    ///         The PATTERN is the legacy one, unchanged, so the token
    ///         boundaries are the regex's own: the two forms count the same
    ///         tokens, and the parity test proves it over the whole tree.
    ///     </para>
    ///     <para>
    ///         The dictionary MUST be built with <see cref="StringComparer.Ordinal" />:
    ///         the alternate lookup compares a span against a string with the
    ///         ORDINAL rule, so a case-insensitive dictionary would disagree
    ///         with its own lookup.
    ///     </para>
    /// </remarks>
    /// <param name="text">The source to count the identifiers of.</param>
    /// <param name="counts">The counts to add to.</param>
    internal static void CountIdentifiers(ReadOnlySpan<char> text, Dictionary<string, int> counts)
    {
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup =
            counts.GetAlternateLookup<ReadOnlySpan<char>>();

        foreach (ValueMatch match in IdentifierPattern.EnumerateMatches(text))
        {
            ReadOnlySpan<char> token = text.Slice(match.Index, match.Length);
            lookup[token] = lookup.TryGetValue(token, out int existing) ? existing + 1 : 1;
        }
    }

    /// <summary>
    ///     Counts the identifiers of a source file into a NEW dictionary: the
    ///     legacy form, kept as the measurement's "before" column.
    /// </summary>
    /// <remarks>
    ///     Kept as the measurement's "before" and as the parity test's
    ///     reference. Do not use this in a rule.
    /// </remarks>
    /// <param name="text">The source to count the identifiers of.</param>
    /// <returns>How often each identifier occurs.</returns>
    internal static Dictionary<string, int> CountIdentifiersLegacy(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match identifier in IdentifierPattern.Matches(text))
        {
            counts[identifier.Value] = counts.GetValueOrDefault(identifier.Value) + 1;
        }

        return counts;
    }
}
