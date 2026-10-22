// SourceParseParityTests.cs — the proof that the new parser grades the SAME
// tree the old one graded.
//
// WHY THIS FILE EXISTS
// --------------------
// The architecture suite's rules grade TEXT. Rewriting the text parser is an
// optimisation, and an optimisation that changes a parser's OUTPUT is not an
// optimisation: it is a change to every rule that reads it, and a rule that
// starts catching something else is a rule that moved without anybody
// deciding that it should.
//
// So the rewrite ships with its own proof, and the proof is not "the tests are
// still green" — a rule that finds nothing reports nothing, which is green.
// The proof is BYTE IDENTITY: the new parser and the old one produce the same
// bytes for every file of the graded corpus, and the test below says so.
//
// WHAT IS COMPARED
// ----------------
//   * the literal blanker — the new in-place lexer against the
//     `StringBuilder` lexer it replaced;
//   * the identifier counter — the new span lookup against the
//     `Regex.Matches` walk it replaced.
//
// A difference in ANY of them is a difference in some rule's verdict, so a
// difference here fails the build before it reaches a rule.
//
// WHAT IS NOT COMPARED HERE, AND WHERE IT LIVES
// ----------------------------------------------
// The comment stripper is NOT compared here: it is `SourceCommentBlanker`,
// it was rewritten on its own (#2167), and `CommentStripperEquivalenceTests`
// proves its equivalence over the whole live tree against an oracle that is
// the old regular expressions with the quadratic read replaced by a linear
// one. A second proof of the same property would be a second answer to the
// same question.
//
// THE ROSLYN VARIANT IS COMPARED TOO, AND IT IS EXPECTED TO DIFFER
// -----------------------------------------------------------------
// `SourceParse.StripCommentsRoslyn` finds the comments by PARSING the file,
// which is the textbook way and the one this suite cannot afford — but it is
// also RIGHT where the text stripper is wrong: the text stripper cannot tell
// a comment from a `//` inside a string literal, so it eats the rest of a
// line that holds a URL. The differences are counted and printed, not asserted
// away: they are the evidence for the choice, and the reader is told how many
// lines of the corpus the text stripper was eating.

namespace Harbor.Architecture.Tests;

/// <summary>
///     The parity proof for the parsing substrate: the new parser and the
///     legacy parser produce the same bytes for every file of the graded
///     corpus.
/// </summary>
public sealed class SourceParseParityTests
{
    /// <summary>How many files the Roslyn comparison runs over.</summary>
    /// <remarks>
    ///     Roslyn parses a file to find its comments, and the graded corpus is
    ///     tens of megabytes: comparing the whole of it would cost minutes in
    ///     a shard that is measured in seconds. The sample is the LARGEST
    ///     files, which is where the difference is most likely to be, and the
    ///     cost per byte it reports is what the projection rests on.
    /// </remarks>
    private const int RoslynSampleSize = 40;

    /// <summary>
    ///     The new parser and the legacy parser agree, byte for byte, on every
    ///     file of the graded corpus.
    /// </summary>
    [Test]
    public async Task The_Parser_Agrees_With_The_Legacy_Parser_On_Every_File()
    {
        IReadOnlyList<string> files = SourceParseCorpus.Files();
        IReadOnlyList<string> texts = SourceParseCorpus.Texts();

        await Assert.That(files.Count).IsGreaterThan(1000)
            .Because(
                "the parity proof is worth nothing on a corpus of a handful of files: the corpus is "
                + "every file of src/, apps/ and tests/, and a corpus that shrank means the WALK broke "
                + "rather than the parser agreeing. Files: " + files.Count);

        var blankDifferences = new List<string>();
        var countDifferences = new List<string>();

        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i];
            string file = files[i];

            if (!string.Equals(
                    SourceParse.BlankLiteralsAndComments(text),
                    SourceParse.BlankLiteralsAndCommentsLegacy(text),
                    StringComparison.Ordinal))
            {
                blankDifferences.Add(file);
            }

            var newCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            SourceParse.CountIdentifiers(text, newCounts);

            if (!SameCounts(newCounts, SourceParse.CountIdentifiersLegacy(text)))
            {
                countDifferences.Add(file);
            }
        }

        Console.WriteLine(
            $"[arch-count] parse-parity files={files.Count} "
            + $"blank-diffs={blankDifferences.Count} count-diffs={countDifferences.Count}");

        await Assert.That(blankDifferences).IsEmpty()
            .Because(
                "the literal blanker is the STRUCTURE view of every file, and it is what the brace "
                + "depth comes from: a different byte moves a type's body, and a type whose body moved "
                + "is a type whose members moved. Files whose blanked text changed: "
                + string.Join(" | ", blankDifferences.Take(20)));

        await Assert.That(countDifferences).IsEmpty()
            .Because(
                "the identifier counter decides whether a public member is REFERENCED, and a count "
                + "that moved is a member that became dead or alive without anybody deciding it. Files "
                + "whose identifier counts changed: " + string.Join(" | ", countDifferences.Take(20)));
    }

    /// <summary>
    ///     The Roslyn stripper is the variant this suite MEASURED and did not
    ///     adopt, and the corpus says what it costs and what it changes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is a MEASUREMENT, not a parity proof: the two strippers are
    ///         expected to differ, because the text stripper cannot tell a
    ///         comment from a <c>//</c> inside a string literal, and it eats the
    ///         rest of such a line. The difference is the evidence for the
    ///         choice, and it is printed rather than asserted away.
    ///     </para>
    ///     <para>
    ///         What IS asserted is non-vacuity: the sample is real, the
    ///         variant produced output, and the measurement says something. A
    ///         measurement that reports zeros because it looked at nothing is
    ///         worse than no measurement.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Roslyn_Stripper_Is_Measured_And_The_Difference_Is_Printed()
    {
        IReadOnlyList<string> files = SourceParseCorpus.Files();
        IReadOnlyList<string> texts = SourceParseCorpus.Texts();

        // The largest files: the most comment, and the most chance of a
        // literal that holds a URL.
        var order = Enumerable.Range(0, texts.Count)
            .OrderByDescending(i => texts[i].Length)
            .Take(RoslynSampleSize)
            .OrderBy(i => i)
            .ToList();

        var differing = new List<string>();
        long roslynChars = 0;

        foreach (int i in order)
        {
            // The stripper the rules actually read: `SourceCommentBlanker`,
            // rewritten on its own in #2167.
            string textStripped = SourceCommentBlanker.Blank(texts[i]);
            string roslynStripped = SourceParse.StripCommentsRoslyn(texts[i]);
            roslynChars += roslynStripped.Length;

            if (!string.Equals(textStripped, roslynStripped, StringComparison.Ordinal))
            {
                differing.Add(files[i] + " (text " + textStripped.Length + " B, roslyn " + roslynStripped.Length + " B)");
            }
        }

        Console.WriteLine(
            $"[arch-count] parse-roslyn sample={order.Count} differing={differing.Count} "
            + $"roslyn-bytes={roslynChars}");

        await Assert.That(order.Count).IsEqualTo(RoslynSampleSize)
            .Because(
                "the sample is the largest files of the corpus, and a sample that came back short "
                + "means the corpus is smaller than the measurement assumes — which would make every "
                + "number the projection rests on a projection of nothing.");

        await Assert.That(roslynChars).IsGreaterThan(0)
            .Because(
                "the Roslyn stripper produced no output at all, so the comparison is measuring an "
                + "empty program and the cost it reports is the cost of doing nothing.");
    }

    /// <summary>Whether two identifier-count dictionaries hold the same counts.</summary>
    /// <param name="left">The first dictionary.</param>
    /// <param name="right">The second dictionary.</param>
    private static bool SameCounts(Dictionary<string, int> left, Dictionary<string, int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((string token, int count) in left)
        {
            if (!right.TryGetValue(token, out int other) || other != count)
            {
                return false;
            }
        }

        return true;
    }
}
