// SourceCommentStripper.cs — blanks out COMMENTS while keeping string
// literals, for the repository text scans that grade source-shaped rules
// (see LogLevelMnemonicRule #563, SessionStatusTableRule #663).
//
// WHY A SCAN NEEDS IT
// -------------------
// A source scan that matches a code SHAPE will happily match the prose that
// documents the very thing it is grading. `StatusMappers` and this file both
// quote `MochaYellow` / `working` in their XML docs; a scanner that reads
// comments would grade each of those doc lines as a second table and then go
// red on the canonical implementation. Stripping comments first is what makes
// "exactly one table" a statement about code.
//
// WHY ONE IMPLEMENTATION
// ----------------------
// Each rule that needs this has its own idea of which literals matter (some
// keep them, some want them gone), but the LEXER is the same and getting it
// subtly wrong is how a scanner starts matching `"// not a table"` or, worse,
// stops matching the table. Two rules, one lexer: when it is wrong, both rules
// are wrong in the same visible way instead of disagreeing quietly.
//
// KEEPING STRING LITERALS
// -----------------------
// The default here is to preserve them, because a rule about a rendered string
// ("working", "MochaYellow", "????") is ABOUT the literal. A rule that wants the
// prose removed but the literals kept is the common case; a rule that wants
// literals gone is rare enough to warrant its own method rather than a flag
// every call site has to think about.

using System.Text;

namespace Harbor.Architecture.Tests;

/// <summary>Lexical states for <see cref="Strip" />.</summary>
internal enum StripState
{
    /// <summary>Ordinary code.</summary>
    Code,

    /// <summary>Inside a <c>"…"</c> string, where <c>\</c> escapes.</summary>
    String,

    /// <summary>Inside an <c>@"…"</c> verbatim string, where <c>""</c> escapes.</summary>
    VerbatimString,

    /// <summary>Inside a <c>'…'</c> char.</summary>
    Char,

    /// <summary>Inside a <c>// …</c> comment, ending at the newline.</summary>
    LineComment,

    /// <summary>Inside a <c>/* … */</c> comment.</summary>
    BlockComment,
}

/// <summary>
///     Line-oriented comment stripper for the architecture test suite's source
///     scans. It is a LINE scanner, not a whole-file one: a <c>/* … */</c>
///     comment spanning several lines is handled by <see cref="StripAll" />,
///     which carries the state across lines. <see cref="Strip" /> assumes each
///     line starts in <see cref="StripState.Code" /> and is what a per-line
///     caller wants.
/// </summary>
internal static class SourceCommentStripper
{
    /// <summary>
    ///     Strips comments from one line, assuming the line starts in code.
    ///     String and char literals survive, escapes included.
    /// </summary>
    internal static string Strip(string line)
    {
        var output = new StringBuilder(line.Length);
        StripState state = StripState.Code;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            switch (state)
            {
                case StripState.Code:
                    if (c == '/' && next == '/')
                    {
                        state = StripState.LineComment;
                        i++;
                        continue;
                    }

                    if (c == '/' && next == '*')
                    {
                        state = StripState.BlockComment;
                        i++;
                        continue;
                    }

                    if (c == '@' && next == '"')
                    {
                        state = StripState.VerbatimString;
                        output.Append(c);
                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        state = StripState.String;
                        output.Append(c);
                        continue;
                    }

                    if (c == '\'')
                    {
                        state = StripState.Char;
                        output.Append(c);
                        continue;
                    }

                    output.Append(c);
                    continue;

                case StripState.String:
                    output.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        output.Append(next);
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = StripState.Code;
                    }

                    continue;

                case StripState.VerbatimString:
                    output.Append(c);
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            output.Append(next);
                            i++;
                        }
                        else
                        {
                            state = StripState.Code;
                        }
                    }

                    continue;

                case StripState.Char:
                    output.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        output.Append(next);
                        i++;
                    }
                    else if (c == '\'')
                    {
                        state = StripState.Code;
                    }

                    continue;

                case StripState.LineComment:
                    if (c == '\n')
                    {
                        state = StripState.Code;
                        output.Append(c);
                    }

                    continue;

                case StripState.BlockComment:
                    if (c == '*' && next == '/')
                    {
                        state = StripState.Code;
                        i++;
                    }

                    continue;

                default:
                    throw new InvalidOperationException($"[source-stripper] unknown strip state {state}.");
            }
        }

        return output.ToString();
    }

    /// <summary>
    ///     Strips comments across a whole file, carrying block-comment state from
    ///     line to line. Returns one stripped line per input line, so line
    ///     numbers still index the result — which is what the rules report.
    /// </summary>
    internal static string[] StripAll(IEnumerable<string> lines)
    {
        var result = new List<string>();
        bool inBlockComment = false;

        foreach (string line in lines)
        {
            if (!inBlockComment)
            {
                string stripped = Strip(line);

                // A line that opened a block comment and did not close it leaves
                // the state machine inside BlockComment. Detect that by counting:
                // a balanced line cannot still be open.
                if (OpensUnterminatedBlockComment(stripped))
                {
                    inBlockComment = true;
                }

                result.Add(stripped);
                continue;
            }

            int close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0)
            {
                result.Add(string.Empty);
                continue;
            }

            // The tail after */ is real code again — strip it on its own so a
            // string that starts there is not misread as comment text.
            inBlockComment = false;
            result.Add(Strip(line[(close + 2)..]));
        }

        return [.. result];
    }

    /// <summary>
    ///     Whether <paramref name="stripped" /> left the lexer inside a block
    ///     comment. Determined by the <c>/*</c> and <c>*/</c> counts in code,
    ///     which is exact because <see cref="Strip" /> has already removed every
    ///     comment that was not the one in question.
    /// </summary>
    private static bool OpensUnterminatedBlockComment(string stripped)
    {
        int opens = 0;
        int closes = 0;
        for (int i = 0; i < stripped.Length; i++)
        {
            if (stripped[i] == '/' && i + 1 < stripped.Length && stripped[i + 1] == '*')
            {
                opens++;
                i++;
            }
            else if (stripped[i] == '*' && i + 1 < stripped.Length && stripped[i + 1] == '/')
            {
                closes++;
                i++;
            }
        }

        return opens > closes;
    }
}
