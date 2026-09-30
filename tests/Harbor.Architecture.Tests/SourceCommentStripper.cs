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
///     scans. It is a LINE scanner that can be made to span lines: a
///     <c>/* … */</c> comment or a <c>@"…"</c> verbatim string that runs over
///     several lines is carried from one to the next by <see cref="StripAll" />,
///     which threads the <see cref="StripState" /> through every line.
///     <see cref="Strip" /> is the per-line form and assumes each line starts in
///     <see cref="StripState.Code" /> — use it only when the input really is one
///     line at a time with no continuation, which for a source scan means never.
/// </summary>
internal static class SourceCommentStripper
{
    /// <summary>
    ///     Strips comments from one line, assuming the line starts in code.
    ///     String and char literals survive, escapes included.
    /// </summary>
    internal static string Strip(string line)
    {
        StripState state = StripState.Code;
        return Strip(line, ref state);
    }

    /// <summary>
    ///     Strips comments from one line, continuing from <paramref name="state" /> and
    ///     leaving it where the line ended. <see cref="StripAll" /> threads one state
    ///     through every line; <see cref="Strip" /> is the same code with a local that
    ///     starts — and is discarded — in <see cref="StripState.Code" />.
    /// </summary>
    /// <remarks>
    ///     Returning the state is the whole mechanism, and the reason is not a
    ///     preference. This method DELETES the comment characters, so the stripped
    ///     text no longer holds the <c>/*</c> that would answer "did this line open a
    ///     block comment" — a question asked afterwards is answered from evidence
    ///     that is already gone. That was #919, and it is why the counter it used is
    ///     gone rather than fixed. See <see cref="StripAll" /> for what that counter
    ///     actually did.
    /// </remarks>
    private static string Strip(string line, ref StripState state)
    {
        var output = new StringBuilder(line.Length);

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

        // A `//` comment ends at the newline, and the LineComment arm above only
        // returns to Code on a `\n` — which never arrives, because every caller
        // hands over lines that have none (`File.ReadAllLines`, `Split('\n')`). So
        // the states that cannot legally span a line are closed here instead.
        // `Char` cannot span a line in C# at all and is closed defensively; the
        // one that matters is `LineComment`, because `///` is the first thing in
        // a C# file. Carry it and the next line is dropped, and so is every line
        // after that, since a carried LineComment never finds a newline either.
        // That is not a subtle degradation: it makes every rule that reads the
        // file see an empty one.
        if (state is StripState.LineComment or StripState.Char)
        {
            state = StripState.Code;
        }

        return output.ToString();
    }

    /// <summary>
    ///     Strips comments across a whole file, carrying the lexer state from line to
    ///     line. Returns one stripped line per input line, so line numbers still index
    ///     the result — which is what the rules report.
    /// </summary>
    /// <remarks>
    ///     The state is carried, not recomputed, and the reason is worth keeping
    ///     because the fix looks like a simplification and is not. <c>Strip</c> DELETES
    ///     real comment characters but PRESERVES string literals, so no inspection of
    ///     the stripped line can recover what it deleted — but a <c>/*</c> that was
    ///     never a comment survives in a literal. The previous version counted
    ///     <c>/*</c> against <c>*/</c> in the stripped text anyway, which made the
    ///     count zero for every genuine block comment and non-zero for every
    ///     <c>"src/*"</c>: exactly inverted, so a multi-line <c>/* … */</c> reached
    ///     every rule as code while a glob pattern in a literal blanked the rest of
    ///     the file. Measured in <c>src/</c> + <c>apps/</c>: 845 lines of real code
    ///     across 5 files, 442 of them in <c>PermissionRuleset.cs</c>. See
    ///     <c>SourceCommentStripperTests</c> (#919).
    /// </remarks>
    internal static string[] StripAll(IEnumerable<string> lines)
    {
        var result = new List<string>();
        StripState state = StripState.Code;

        foreach (string line in lines)
        {
            result.Add(Strip(line, ref state));
        }

        return [.. result];
    }
}
