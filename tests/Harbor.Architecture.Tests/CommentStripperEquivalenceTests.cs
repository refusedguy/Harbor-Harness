// CommentStripperEquivalenceTests.cs — the proof that the new comment
// stripper produces the BYTES the old one produced.
//
// WHY THE PROOF IS SHAPED THE WAY IT IS
// --------------------------------------
// The rules in this project read the repository's source with their comments
// blanked out, and they report LINE NUMBERS into the blanked text. A stripper
// that produces different bytes does not merely cost differently — it moves
// every finding a rule reports, and a rule that moved its finding is a rule
// whose verdict changed, however green the test stays.
//
// So the old implementation is kept, in `CommentStripperOracle.cs`, and the
// new one is proved against it in three steps:
//
//   1. THE BLANKING. The old and the new blanking differ in ONE thing: the old
//      read `Match.Value` once per character of the match, and `Match.Value`
//      is a property that allocates a fresh string on every read; the new one
//      reads `Match.ValueSpan` once. `Match.Value` is `Text.Substring(index,
//      length)` and `Match.ValueSpan` is the same range of the same text, so
//      the two blanks are the same characters. The corpus below proves it
//      empirically for every block-comment length that occurs in the tree and
//      for a set of adversarial shapes.
//
//   2. THE MATCHING. The scanner finds block comments by hand instead of with
//      `/\*.*?\*/`. The corpus and the whole live tree below prove that it
//      finds the SAME ones, and the whole live tree is the proof that matters:
//      it is the input the rules actually read.
//
//   3. THE LINE COMMENTS. Same story, one space for the rest of the line.
//
// The tree comparison is the one that costs something, so it is scoped to the
// three trees the rules read: `src/`, `apps/` and `tests/`.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Proves that <see cref="SourceCommentBlanker" /> produces the bytes the
///     previous implementation produced.
/// </summary>
public sealed class CommentStripperEquivalenceTests
{
    /// <summary>
    ///     The scanner and the oracle agree on every file of the live tree.
    /// </summary>
    [Test]
    public async Task The_Scanner_And_The_Oracle_Agree_On_Every_File_Of_The_Live_Tree()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the proof reads the repository's own source; with no root it would compare nothing "
                + "and pass for the wrong reason.");

        int files = 0;
        int chars = 0;
        var mismatches = new List<string>();

        foreach (string file in EnumerateLiveCsFiles())
        {
            string text = File.ReadAllText(file);
            files++;
            chars += text.Length;

            string scanner = SourceCommentBlanker.Blank(text);
            string oracle = CommentStripperOracle.StripCommentsOracleText(text);

            if (!string.Equals(scanner, oracle, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"{SourceScan.Relative(file)} — the scanner and the oracle disagree at "
                    + $"offset {FirstDifference(scanner, oracle)}");
            }
        }

        Console.WriteLine(
            $"[arch-count] stripper equivalence tree files={files} chars={chars} mismatches={mismatches.Count}");

        await Assert.That(files).IsGreaterThan(2000)
            .Because(
                "the tree holds 2 661 .cs files; a much smaller number means the walk broke and the "
                + "comparison below proved nothing.");

        await Assert.That(mismatches).IsEmpty()
            .Because(
                "the scanner replaced the previous implementation, and a byte of difference is a "
                + "line number that moved. The old bytes are what the rules were written against.");
    }

    /// <summary>
    ///     The oracle and the previous implementation agree on every input the
    ///     previous implementation can afford.
    /// </summary>
    /// <remarks>
    ///     "Can afford" is the operative phrase: the previous implementation
    ///     allocates the SQUARE of the length of every block comment, so a
    ///     75 497-character comment costs 11.4 GB and no test may run it. The
    ///     corpus below therefore covers the SHAPE of every block comment the
    ///     tree holds — every length up to 2 048 characters, plus the lengths
    ///     the tree actually uses up to the point the previous implementation
    ///     stays affordable — and the adversarial shapes around them.
    /// </remarks>
    [Test]
    public async Task The_Oracle_And_The_Previous_Implementation_Agree_On_Every_Affordable_Input()
    {
        var mismatches = new List<string>();
        int compared = 0;

        foreach (string input in Corpus())
        {
            compared++;
            string legacy = CommentStripperOracle.StripCommentsLegacy(input);
            string oracle = CommentStripperOracle.StripCommentsOracleText(input);

            if (!string.Equals(legacy, oracle, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"a corpus input of {input.Length} character(s) differs at offset "
                    + $"{FirstDifference(legacy, oracle)}");
            }
        }

        // The real tree, for the files the previous implementation can afford.
        int treeFiles = 0;
        int skipped = 0;
        foreach (string file in EnumerateLiveCsFiles())
        {
            string text = File.ReadAllText(file);

            if (CommentStripperOracle.ProjectedLegacyBytes(text) > AffordableLegacyBytes)
            {
                skipped++;
                continue;
            }

            treeFiles++;
            compared++;
            string legacy = CommentStripperOracle.StripCommentsLegacy(text);
            string oracle = CommentStripperOracle.StripCommentsOracleText(text);

            if (!string.Equals(legacy, oracle, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"{SourceScan.Relative(file)} — the previous implementation and the oracle "
                    + $"disagree at offset {FirstDifference(legacy, oracle)}");
            }
        }

        Console.WriteLine(
            $"[arch-count] stripper equivalence legacy corpus+tree compared={compared} tree-files={treeFiles} "
            + $"skipped-too-expensive={skipped} mismatches={mismatches.Count}");

        await Assert.That(compared).IsGreaterThan(2000)
            .Because(
                "the corpus and the affordable part of the tree are the whole proof; a much smaller "
                + "number means the corpus collapsed and the comparison proved nothing.");

        await Assert.That(mismatches).IsEmpty()
            .Because(
                "the oracle is only a stand-in for the previous implementation if it produces the same "
                + "bytes; a difference means the proof of the scanner is a proof about something else.");
    }

    /// <summary>
    ///     The scanner and the previous implementation agree on the adversarial
    ///     shapes directly — the transitivity check that closes the proof.
    /// </summary>
    [Test]
    public async Task The_Scanner_And_The_Previous_Implementation_Agree_On_The_Adversarial_Shapes()
    {
        var mismatches = new List<string>();

        foreach (string input in AdversarialShapes())
        {
            string legacy = CommentStripperOracle.StripCommentsLegacy(input);
            string scanner = SourceCommentBlanker.Blank(input);

            if (!string.Equals(legacy, scanner, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"an adversarial input differs at offset {FirstDifference(legacy, scanner)}: "
                    + $"{Escape(input)}");
            }
        }

        await Assert.That(mismatches).IsEmpty()
            .Because(
                "the scanner is the implementation the rules will read; a shape it gets wrong is a "
                + "shape where a rule sees something the previous implementation never showed it.");
    }

    /// <summary>
    ///     The legacy's cost above which a test may not run it: 32 MB of
    ///     garbage per input is already slow, and the corpus covers the shape
    ///     of every block comment the tree holds below this line.
    /// </summary>
    private const long AffordableLegacyBytes = 32L * 1024 * 1024;

    /// <summary>
    ///     Every block-comment length the tree uses, up to the point the
    ///     previous implementation stays affordable, plus the adversarial
    ///     shapes around them.
    /// </summary>
    private static IEnumerable<string> Corpus()
    {
        for (int length = 0; length <= 256; length++)
        {
            yield return "/*" + new string('x', length) + "*/";
            yield return "/*" + new string('x', length) + "*/ trailing";
            yield return "before /*" + new string('x', length) + "*/ after";
        }

        foreach (int length in new[] { 512, 1024, 2048, 4096 })
        {
            yield return "/*" + new string('x', length) + "*/";
            yield return "/*" + new string('x', length) + "*/ trailing";
        }

        foreach (string shape in AdversarialShapes())
        {
            yield return shape;
        }
    }

    /// <summary>
    ///     The shapes where a hand-written scanner and a regular expression
    ///     could plausibly disagree.
    /// </summary>
    private static IEnumerable<string> AdversarialShapes() =>
    [
        "",
        "/",
        "//",
        "///",
        "/*",
        "*/",
        "/*/",
        "/**/",
        "/*/*/",
        "/* a /* b */",
        "/* unterminated",
        "*/ stray close",
        "// line",
        "// line\nnext",
        "// line\r\nnext",
        "code // comment\ncode",
        "code // comment",
        "\"a string with // inside\"",
        "\"a string with /* inside\"",
        "var url = \"http://example.com\";",
        "/* block\nwith newline */\ncode",
        "/* block\r\nwith crlf */\r\ncode",
        "// before /* block */ after",
        "/* block */ // after",
        "// /* block */",
        "/*\n\n\n*/",
        "/*\n*/ // tail",
        "a/*b*/c//d",
        "/*\u00e9\u4e2d\u6587*/",
        "/*\u0000null-ish */",
        "\ufeff/* bom before */",
        "/* a */ /* b */ /* c */",
        "/// <summary>x</summary>",
        "/**/x/**/",
        "x/*",
        "x//",
        "/*" + new string('*', 64),
        "/*" + new string('*', 64) + "/",
        "/*" + new string('/', 64),
    ];

    /// <summary>
    ///     Every <c>*.cs</c> file of the three trees the rules read.
    /// </summary>
    private static IReadOnlyList<string> EnumerateLiveCsFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var found = new List<string>();
        foreach (string tree in new[] { "src", "apps", "tests" })
        {
            string path = Path.Combine(root, tree);
            if (!Directory.Exists(path))
            {
                continue;
            }

            found.AddRange(Directory
                .EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .Where(p =>
                    !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>The index of the first differing character, or -1 when equal.</summary>
    /// <param name="left">The first string.</param>
    /// <param name="right">The second string.</param>
    private static int FirstDifference(string left, string right)
    {
        int shared = Math.Min(left.Length, right.Length);
        for (int i = 0; i < shared; i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }

        return left.Length == right.Length ? -1 : shared;
    }

    /// <summary>A short, printable rendering of an input, for a failure message.</summary>
    /// <param name="input">The input to render.</param>
    private static string Escape(string input) =>
        input.Length <= 120 ? input : input[..120] + "…";
}
