// ProcessEnvIsolationParityTests.cs — the proof that the cheap pre-checks the
// process-environment rule runs before its per-line work did not move a
// verdict.
//
// WHAT THE PRE-CHECKS ARE
// -----------------------
// `ProcessEnvIsolationRule` walks EVERY line of EVERY test project, and three
// of its questions are asked per line:
//
//   * is this line a type declaration?       (`TypeDeclarationMatch`)
//   * does this line call the env write?     (`IsEnvWrite`)
//   * does this line name an ambient read?   (`IsAmbientRead`)
//
// The first and the third used to run a regular expression over every line of
// the tree, and the second used to strip the line's comments and its literal
// contents — a buffer and a string PER LINE — before asking a question whose
// answer is "no" for all but a handful of lines.
//
// Each of them now asks a CHEAP NECESSARY CONDITION first:
//
//   * a type declaration names `class`, `record` or `struct`;
//   * an env write names `Environment.SetEnvironmentVariable(`;
//   * an ambient read names `Environment.GetEnvironmentVariable(` or
//     `Environment.GetFolderPath(`.
//
// WHY A PARITY PROOF, AND NOT "THE TESTS ARE STILL GREEN"
// -------------------------------------------------------
// The rule's own tests assert its verdicts on planted fixtures and on the
// live tree's writer set. A pre-check that was wrong in the direction of
// "finds less" would narrow that set, and the non-vacuity test ("at least
// eleven writer files") would catch it — but only while the tree keeps its
// shape. The proof here is stronger and simpler: over EVERY line of EVERY
// file of the live tree, the pre-checked answer equals the answer the
// previous implementation gave.
//
// THE PREVIOUS IMPLEMENTATION, VERBATIM
// -------------------------------------
// The references below are the old code, copied unchanged:
//
//     StripLiterals(SourceScan.StripComments(rawLine))
//         .Contains(EnvWriteCall, StringComparison.Ordinal)
//
//     AnyAmbientRead.IsMatch(StripLiterals(SourceScan.StripComments(line)))
//
//     TypeDeclaration.IsMatch(line)
//
// A twin that is a paraphrase is not a twin.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Proves that the cheap pre-checks the process-environment rule runs before
///     its per-line work report exactly what the work itself reports, on every
///     line of the live tree.
/// </summary>
public sealed class ProcessEnvIsolationParityTests
{
    /// <summary>
    ///     The pre-checked env-write answer equals the un-pre-checked one, on
    ///     every line of every file of the live tree.
    /// </summary>
    [Test]
    public async Task The_Env_Write_Pre_Check_Agrees_With_The_Strip_On_Every_Line()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the proof reads the repository's own source; with no root it would compare nothing "
                + "and pass for the wrong reason.");

        (int lines, int positives, List<string> mismatches) = CompareOverTheTree(
            ProcessEnvIsolationRule.IsEnvWrite,
            ReferenceIsEnvWrite);

        Console.WriteLine(
            $"[arch-count] process-env parity lines={lines} env-writes={positives} mismatches={mismatches.Count}");

        await Assert.That(lines).IsGreaterThan(50_000)
            .Because(
                "the rule walks every line of every test project, and that is more than fifty thousand "
                + "lines; a smaller number means the walk broke and the comparison proved nothing.");
        await Assert.That(positives).IsGreaterThan(0)
            .Because(
                "the tree does contain env writes, and a comparison that found none would be a "
                + "comparison that proved nothing.");
        await Assert.That(mismatches.ToArray()).IsEmpty()
            .Because(
                "the pre-check may only skip a line the strip would have rejected anyway: "
                + string.Join(" | ", mismatches));
    }

    /// <summary>
    ///     A line that names no type keyword is not a type declaration, on every
    ///     line of every file of the live tree.
    /// </summary>
    [Test]
    public async Task The_Type_Keyword_Pre_Check_Agrees_With_The_Pattern_On_Every_Line()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the proof reads the repository's own source; with no root it would compare nothing "
                + "and pass for the wrong reason.");

        (int lines, int positives, List<string> mismatches) = CompareOverTheTree(
            (line) => ProcessEnvIsolationRule.TypeDeclarationMatch(line) is { Success: true },
            (line) => ProcessEnvIsolationRule.TypeDeclaration.IsMatch(line));

        Console.WriteLine(
            $"[arch-count] process-env parity lines={lines} declarations={positives} "
            + $"mismatches={mismatches.Count}");

        await Assert.That(lines).IsGreaterThan(50_000)
            .Because(
                "the rule walks every line of every test project, and that is more than fifty thousand "
                + "lines; a smaller number means the walk broke and the comparison proved nothing.");
        await Assert.That(positives).IsGreaterThan(100)
            .Because(
                "the tree holds more than a hundred type declarations, and a comparison that found "
                + "none would be a comparison that proved nothing.");
        await Assert.That(mismatches.ToArray()).IsEmpty()
            .Because(
                "the pre-check may only skip a line the pattern would have rejected anyway: "
                + string.Join(" | ", mismatches));
    }

    /// <summary>
    ///     A line that names no ambient read is not an ambient read, on every
    ///     line of every file of the live tree.
    /// </summary>
    [Test]
    public async Task The_Ambient_Read_Pre_Check_Agrees_With_The_Pattern_On_Every_Line()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the proof reads the repository's own source; with no root it would compare nothing "
                + "and pass for the wrong reason.");

        (int lines, int positives, List<string> mismatches) = CompareOverTheTree(
            ProcessEnvIsolationRule.IsAmbientRead,
            ReferenceIsAmbientRead);

        Console.WriteLine(
            $"[arch-count] process-env parity lines={lines} ambient-reads={positives} "
            + $"mismatches={mismatches.Count}");

        await Assert.That(lines).IsGreaterThan(50_000)
            .Because(
                "the rule walks every line of every test project, and that is more than fifty thousand "
                + "lines; a smaller number means the walk broke and the comparison proved nothing.");
        await Assert.That(mismatches.ToArray()).IsEmpty()
            .Because(
                "the pre-check may only skip a line the pattern would have rejected anyway: "
                + string.Join(" | ", mismatches));
    }

    /// <summary>
    ///     What the three pre-checks save: the old questions and the new ones,
    ///     over every line of the live tree, measured side by side in the same
    ///     run.
    /// </summary>
    [Test]
    public async Task Measure_What_The_Pre_Checks_Save()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the measurement reads the repository's own source; with no root it would measure "
                + "nothing and report a cost of zero that looks like a free scan.");

        long envWriteBefore = 0;
        long envWriteAfter = 0;
        long declarationBefore = 0;
        long declarationAfter = 0;
        long readBefore = 0;
        long readAfter = 0;
        int lines = 0;

        foreach (string file in EnumerateTestSources())
        {
            SourceTreeFile? source = SharedSourceTreeSnapshot.Shared.Get(file);
            if (source is null)
            {
                continue;
            }

            foreach (string line in source.StrippedReadLineLines)
            {
                lines++;

                long mark = GC.GetAllocatedBytesForCurrentThread();
                _ = ReferenceIsEnvWrite(line);
                envWriteBefore += GC.GetAllocatedBytesForCurrentThread() - mark;

                mark = GC.GetAllocatedBytesForCurrentThread();
                _ = ProcessEnvIsolationRule.IsEnvWrite(line);
                envWriteAfter += GC.GetAllocatedBytesForCurrentThread() - mark;

                mark = GC.GetAllocatedBytesForCurrentThread();
                _ = ProcessEnvIsolationRule.TypeDeclaration.IsMatch(line);
                declarationBefore += GC.GetAllocatedBytesForCurrentThread() - mark;

                mark = GC.GetAllocatedBytesForCurrentThread();
                _ = ProcessEnvIsolationRule.TypeDeclarationMatch(line) is { Success: true };
                declarationAfter += GC.GetAllocatedBytesForCurrentThread() - mark;

                mark = GC.GetAllocatedBytesForCurrentThread();
                _ = ReferenceIsAmbientRead(line);
                readBefore += GC.GetAllocatedBytesForCurrentThread() - mark;

                mark = GC.GetAllocatedBytesForCurrentThread();
                _ = ProcessEnvIsolationRule.IsAmbientRead(line);
                readAfter += GC.GetAllocatedBytesForCurrentThread() - mark;
            }
        }

        Report("process-env-env-write", envWriteBefore, envWriteAfter, lines);
        Report("process-env-type-declaration", declarationBefore, declarationAfter, lines);
        Report("process-env-ambient-read", readBefore, readAfter, lines);
    }

    /// <summary>
    ///     One pair's numbers, in the form the job summary carries.
    /// </summary>
    /// <param name="name">The fix's name, as the report carries it.</param>
    /// <param name="before">The previous implementation's bytes.</param>
    /// <param name="after">The new implementation's bytes.</param>
    /// <param name="lines">How many lines the measurement ran over.</param>
    private static void Report(string name, long before, long after, int lines)
    {
        double ratio = after == 0 ? 0 : (double)before / after;
        Console.WriteLine(
            $"[arch-cost] fix={name} before-bytes={before} after-bytes={after} ratio={ratio:F2} lines={lines}");
    }

    /// <summary>
    ///     The previous implementation of the env-write question, verbatim.
    /// </summary>
    /// <param name="rawLine">The line to ask about.</param>
    private static bool ReferenceIsEnvWrite(string rawLine) =>
        ProcessEnvIsolationRule.StripLiterals(SourceScan.StripComments(rawLine))
            .Contains(ProcessEnvIsolationRule.EnvWriteCall, StringComparison.Ordinal);

    /// <summary>
    ///     The previous implementation of the ambient-read question, verbatim.
    /// </summary>
    /// <param name="line">The line to ask about.</param>
    private static bool ReferenceIsAmbientRead(string line) =>
        ProcessEnvIsolationRule.AnyAmbientRead.IsMatch(
            ProcessEnvIsolationRule.StripLiterals(SourceScan.StripComments(line)));

    /// <summary>
    ///     Runs two questions over every line of every file of the live tree and
    ///     reports where they disagree.
    /// </summary>
    /// <param name="actual">The question as the rule now asks it.</param>
    /// <param name="reference">The question as the rule used to ask it.</param>
    private static (int Lines, int Positives, List<string> Mismatches) CompareOverTheTree(
        Func<string, bool> actual,
        Func<string, bool> reference)
    {
        int lines = 0;
        int positives = 0;
        var mismatches = new List<string>();

        foreach (string file in EnumerateTestSources())
        {
            SourceTreeFile? source = SharedSourceTreeSnapshot.Shared.Get(file);
            if (source is null)
            {
                continue;
            }

            foreach (string line in source.StrippedReadLineLines)
            {
                lines++;

                bool now = actual(line);
                if (now)
                {
                    positives++;
                }

                if (now != reference(line) && mismatches.Count < 20)
                {
                    mismatches.Add(
                        $"{SourceScan.Relative(file)} — the pre-checked answer and the un-pre-checked "
                        + $"answer differ on a line that mentions \"{Shorten(line)}\".");
                }
            }
        }

        return (lines, positives, mismatches);
    }

    /// <summary>
    ///     Every <c>*.cs</c> under <c>tests/</c>, minus build output and sibling
    ///     worktrees — a superset of the rule's own scope, which is the test
    ///     projects.
    /// </summary>
    private static IReadOnlyList<string> EnumerateTestSources()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string tests = Path.Combine(root, "tests");
        if (!Directory.Exists(tests))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>A line, shortened for a failure message.</summary>
    /// <param name="line">The line to shorten.</param>
    private static string Shorten(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
    }
}
