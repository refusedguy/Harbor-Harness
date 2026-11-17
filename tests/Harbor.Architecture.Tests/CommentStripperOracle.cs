// CommentStripperOracle.cs — the OLD comment stripper, kept as the ORACLE
// the new one is proved against, and the meter that counts what the rules
// actually ask for.
//
// WHY THE OLD IMPLEMENTATION IS STILL HERE
// ----------------------------------------
// The new stripper (`SourceCommentBlanker`) is only allowed to be faster. It
// is not allowed to be DIFFERENT: the rules read line numbers out of the
// blanked text, and a stripper that shifts a line moves every finding a rule
// reports. So the old implementation stays, as the thing the new one is
// compared against, and it is used in exactly two places:
//
//   1. `StripCommentsLegacy` — the old code VERBATIM, quadratic read
//      included. It is the ground truth for the bytes. It is affordable only
//      on inputs whose block comments are small, because that is the whole
//      point of the exercise.
//
//   2. `StripCommentsOracleText` — the same two regular expressions, with the
//      quadratic read replaced by a linear one (`Match.ValueSpan`, read once).
//      The bytes it produces are the bytes `StripCommentsLegacy` produces —
//      `Match.Value` IS `Text.Substring(index, length)`, so `match.Value[i]`
//      and `match.ValueSpan[i]` are the same character — and it is affordable
//      on the WHOLE tree.
//
// The proof is transitive and lives in `CommentStripperEquivalenceTests.cs`:
// the new stripper equals the oracle over the whole live tree, and the oracle
// equals the legacy over every input the legacy can afford, which includes
// the shape of the one block comment that no affordable input could hold.
//
// THE METER
// ---------
// `StripCommentsMeter` counts what the RULES ask the stripper for, per
// process. The counts are printed when the process exits, because the
// question they answer — "how many times does a shard strip the tree?" — is
// about the whole run and not about one test. Before the shared snapshot
// (#1791) every rule kept its own copy; the numbers below are what the
// snapshot left.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Counts what the rules ask the comment stripper for, over the whole
///     process.
/// </summary>
internal static class StripCommentsMeter
{
    private static long _calls;
    private static long _inputChars;
    private static long _bigCalls;
    private static long _bigInputChars;

    /// <summary>The length at which a call counts as "a whole file".</summary>
    private const int BigCallThreshold = 4096;

    /// <summary>How many times the rules asked for a strip.</summary>
    internal static long Calls => Interlocked.Read(ref _calls);

    /// <summary>How many characters the rules asked to strip.</summary>
    internal static long InputChars => Interlocked.Read(ref _inputChars);

    /// <summary>How many of those calls were whole-file sized.</summary>
    internal static long BigCalls => Interlocked.Read(ref _bigCalls);

    /// <summary>How many characters those whole-file sized calls carried.</summary>
    internal static long BigInputChars => Interlocked.Read(ref _bigInputChars);

    /// <summary>Counts one call.</summary>
    /// <param name="length">The length of the text the call was given.</param>
    internal static void Count(int length)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _inputChars, length);

        if (length >= BigCallThreshold)
        {
            Interlocked.Increment(ref _bigCalls);
            Interlocked.Add(ref _bigInputChars, length);
        }
    }

    /// <summary>
    ///     Prints the counts when the process exits.
    /// </summary>
    /// <remarks>
    ///     The question the counts answer — "how many times does a shard strip
    ///     the tree?" — is about the whole run, not about one test, and the
    ///     process exit is the only moment the whole run is over. The lines
    ///     carry the `[arch-cost]` prefix, so `tools/test-measurements.py`
    ///     picks them up if a test happens to print them into a TRX.
    /// </remarks>
    static StripCommentsMeter() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Console.WriteLine(
                "[arch-cost] stripper process calls=" + Calls
                + " input-chars=" + InputChars
                + " big-calls=" + BigCalls
                + " big-chars=" + BigInputChars);
}

/// <summary>
///     The OLD comment stripper, kept as the oracle the new one is proved
///     against.
/// </summary>
internal static class CommentStripperOracle
{
    /// <summary>Matches one or more consecutive single-line comments.</summary>
    private static readonly Regex LineComment = new("//[^\n]*", RegexOptions.Compiled);

    /// <summary>Matches a C-style block comment, including the newlines it spans.</summary>
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    ///     The implementation the rules ran against before the stripper moved:
    ///     the old regular expressions, with the quadratic read intact.
    /// </summary>
    /// <remarks>
    ///     Affordable only on inputs whose block comments are small — which is
    ///     the defect this file exists to document. On the repository's own tree
    ///     it allocates 12.82 GB.
    /// </remarks>
    /// <param name="source">The text to strip.</param>
    /// <returns>The text with its comments blanked out.</returns>
    internal static string StripCommentsLegacy(string source)
    {
        Interlocked.Increment(ref _legacyCalls);
        return LineComment.Replace(BlockComment.Replace(source, BlankOutCommentLegacy), " ");
    }

    /// <summary>
    ///     How many times the quadratic implementation has been called.
    /// </summary>
    /// <remarks>
    ///     The counter is what makes the budget of
    ///     <c>ParseCostMeasurement</c> checkable: the legacy stripper is
    ///     quadratic, so a file with a 75 497-character comment costs 11.4 GB
    ///     in ONE call, and the measurement that measures the cost of that
    ///     implementation must not RUN it on the files it has decided not to
    ///     count. The number of calls is the evidence that it did not.
    /// </remarks>
    internal static long LegacyCalls => Interlocked.Read(ref _legacyCalls);

    /// <summary>How many times the quadratic implementation has been called.</summary>
    private static long _legacyCalls;

    /// <summary>
    ///     The old implementation with the quadratic read replaced by a linear
    ///     one. The bytes are identical: <c>Match.Value</c> returns
    ///     <c>Text.Substring(index, length)</c>, so <c>match.Value[i]</c> and
    ///     <c>match.ValueSpan[i]</c> are the same character.
    /// </summary>
    /// <param name="source">The text to strip.</param>
    /// <returns>The text with its comments blanked out.</returns>
    internal static string StripCommentsOracleText(string source) =>
        LineComment.Replace(BlockComment.Replace(source, BlankOutCommentLinear), " ");

    /// <summary>
    ///     Blanks one block comment, reading the match's text once per
    ///     character — the old way.
    /// </summary>
    /// <param name="match">The block comment match.</param>
    private static string BlankOutCommentLegacy(Match match)
    {
        var blank = new char[match.Length];
        for (int i = 0; i < match.Length; i++)
        {
            blank[i] = match.Value[i] == '\n' ? '\n' : ' ';
        }

        return new string(blank);
    }

    /// <summary>
    ///     Blanks one block comment, reading the match's text once.
    /// </summary>
    /// <param name="match">The block comment match.</param>
    private static string BlankOutCommentLinear(Match match)
    {
        var blank = new char[match.Length];
        ReadOnlySpan<char> value = match.ValueSpan;
        for (int i = 0; i < blank.Length; i++)
        {
            blank[i] = value[i] == '\n' ? '\n' : ' ';
        }

        return new string(blank);
    }

    /// <summary>
    ///     The legacy implementation's cost over a text, PROJECTED: it
    ///     allocates the square of every block comment's length, in characters
    ///     of garbage, because it reads <c>Match.Value</c> once per character.
    /// </summary>
    /// <remarks>
    ///     The projection is exact — the legacy's cost is a function of the
    ///     block comments' lengths alone — and it is what lets a measurement
    ///     report the cost of a file it may not afford to run.
    /// </remarks>
    /// <param name="text">The text to project the cost of.</param>
    internal static long ProjectedLegacyBytes(string text)
    {
        long total = 0;
        int i = 0;

        while (i < text.Length - 1)
        {
            if (text[i] != '/' || text[i + 1] != '*')
            {
                i++;
                continue;
            }

            int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                i++;
                continue;
            }

            long length = close + 2 - i;
            total += (2 * length * length) + (4 * length);
            i = close + 2;
        }

        return total;
    }
}
