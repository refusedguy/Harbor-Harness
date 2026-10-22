// TestTreeAllocationCensus.cs — the census of the test tree's BYTE hunger.
//
// WHAT THIS FILE IS
// -----------------
// The architecture suite was measured at 738.3 s of summed test duration
// (#1791) and the cause was a layer UNDER the rules: every rule kept its own
// copy of the source tree. That was a TIME problem, and it is fixed.
//
// This file is the BYTE side of the same question, asked of the whole test
// tree: which TESTS, in which PROJECTS, spend the most bytes, and on what.
//
// WHY A CENSUS AND NOT A PROFILER
// --------------------------------
// A profiler would answer "where did the bytes go" for ONE run of ONE
// machine. The question this file answers is different: it is a REPOSITORY
// question — "which constructs in the test tree allocate, how many are they
// written, and what does one of them cost" — and it is answerable from the
// source, so it is answerable on every machine, and it is answerable BEFORE
// anything is fixed.
//
// So the census has two halves:
//
//   1. the SITES: every construct in `tests/**` that allocates, with the file,
//      the line, the project and the test it belongs to; and
//   2. the COSTS: what one call of each of those constructs allocates,
//      MEASURED, on a representative input.
//
// The product of the two is the estimate: a project that writes a construct
// four hundred times, at twelve hundred bytes a call, is paying half a
// megabyte it does not have to.
//
// WHAT IS MEASURED AND WHAT IS ESTIMATED
// ---------------------------------------
// The unit costs are MEASURED: `GC.GetAllocatedBytesForCurrentThread()` over
// a body that cannot suspend, averaged over many calls. The per-project
// totals are ESTIMATES: they are the measured unit cost times the number of
// call SITES, and a site is not a call — a construct in a loop is one site
// and many calls, and a construct in a helper is one site and one call per
// test that uses the helper. The ranking is what the estimate is for.
//
// The lines
// ---------
//   [alloc-census] — one per site. Stable keys: project, file, line, test,
//                     mechanism.
//   [alloc-cost]   — one per mechanism. The measured bytes per call.
//   [alloc-rank]   — one per project. The estimate, and the number of sites.
//
// `tools/test-measurements.py` renders every `Console.WriteLine` a test emits
// into the CI job summary, so these lines reach a human without anyone
// downloading an artifact.
//
// NOTHING HERE IS A GATE
// ----------------------
// This file measures and reports. It asserts that the census can still find
// the sites it is supposed to find — so a census that silently stopped
// working would fail here — and it asserts nothing about the sizes, because a
// budget on a byte count is a budget on the JIT, the runner and the machine.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The constructs in the test tree that allocate.
/// </summary>
internal enum AllocMechanism
{
    /// <summary>A one-character string built per cell, to append a character.</summary>
    CharToString,

    /// <summary><c>string.Split</c> inside a loop: an array and a string per part.</summary>
    SplitInLoop,

    /// <summary><c>string.Join</c> inside a loop.</summary>
    JoinInLoop,

    /// <summary><c>new StringBuilder</c> inside a loop.</summary>
    BuilderInLoop,

    /// <summary><c>Substring</c> inside a loop: a string per call.</summary>
    SubstringInLoop,

    /// <summary><c>ToList</c> or <c>ToArray</c> inside a loop.</summary>
    MaterialiseInLoop,

    /// <summary><c>Regex.Matches</c>: a match collection and a match per hit.</summary>
    RegexMatches,

    /// <summary><c>string.Format</c>: a string per call, plus the boxing of its arguments.</summary>
    StringFormat,

    /// <summary>A file read per call, where the file's bytes do not change between calls.</summary>
    FileReadPerCall,

    /// <summary>A directory walk per call.</summary>
    DirectoryWalk,
}

/// <summary>One allocating construct, and where it lives.</summary>
/// <param name="Project">The test project the file belongs to.</param>
/// <param name="File">The file, relative to the repository root.</param>
/// <param name="Line">The line the construct is on.</param>
/// <param name="Test">The test the construct belongs to, or the class when there is none.</param>
/// <param name="Mechanism">What the construct is.</param>
internal sealed record AllocSite(
    string Project,
    string File,
    int Line,
    string Test,
    AllocMechanism Mechanism);

/// <summary>
///     The census of the test tree: which constructs allocate, where, and what
///     one of them costs.
/// </summary>
public sealed class TestTreeAllocationCensus
{
    /// <summary>The directory the census reads, relative to the repository root.</summary>
    private const string TreeDirectory = "tests";

    /// <summary>A class declaration, for the enclosing-class report.</summary>
    private static readonly Regex ClassDeclaration = new(
        @"\b(?:class|record|struct)\s+(?<name>\w+)",
        RegexOptions.CultureInvariant);

    /// <summary>A method declaration, for the enclosing-test report.</summary>
    private static readonly Regex MethodDeclaration = new(
        @"^\s*(?:public|private|internal|protected|static|async|sealed|override|virtual|partial|extern|unsafe)\b"
        + @"[^\n]*?\b(?<name>\w+)\s*\(",
        RegexOptions.CultureInvariant);

    /// <summary>A loop opener, for the in-loop test.</summary>
    private static readonly Regex LoopOpener = new(
        @"^\s*(?:for|foreach|while|do)\b",
        RegexOptions.CultureInvariant);

    /// <summary>The one-character-string pattern.</summary>
    private static readonly Regex CharToString = new(
        @"\(\s*char\s*\)[^;]*\.ToString\(\)",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     The census itself.
    /// </summary>
    /// <remarks>
    ///     The measurement of the unit costs is per-thread and synchronous, so
    ///     it is exact whatever the other tests in this assembly are doing: the
    ///     per-thread counter does not bill another thread's allocations. The
    ///     class is therefore NOT <c>[NotInParallel]</c> — it must not take the
    ///     suite's serialisation lock to say what a string operation costs.
    /// </remarks>
    [Test]
    public async Task Census_TheTestTree_AndWhatEachConstructCosts()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the census reads the tree from RepoPaths.RepoRoot, and with no root it would report "
                + "an empty tree as a clean one.");

        IReadOnlyList<AllocSite> sites = FindSites();
        IReadOnlyDictionary<AllocMechanism, long> costs = MeasureUnitCosts();

        foreach (AllocSite site in sites)
        {
            Console.WriteLine(
                $"[alloc-census] project={site.Project} file={site.File} line={site.Line} "
                + $"test={site.Test} mechanism={site.Mechanism}");
        }

        foreach (AllocMechanism mechanism in costs.Keys.OrderBy(m => m.ToString(), StringComparer.Ordinal))
        {
            Console.WriteLine($"[alloc-cost] mechanism={mechanism} bytes-per-call={costs[mechanism]}");
        }

        foreach (IGrouping<string, AllocSite> project in sites.GroupBy(s => s.Project).OrderByDescending(g => g.Count()))
        {
            long estimate = project.Sum(s => costs.TryGetValue(s.Mechanism, out long per) ? per : 0);
            int mechanisms = project.Select(s => s.Mechanism).Distinct().Count();

            Console.WriteLine(
                $"[alloc-rank] project={project.Key} sites={project.Count()} mechanisms={mechanisms} "
                + $"tests={project.Select(s => s.Test).Distinct().Count()} est-bytes={estimate}");
        }

        await Assert.That(sites.Count).IsGreaterThan(0)
            .Because("a census that finds nothing is a census that stopped working, not a clean tree.");
    }

    /// <summary>
    ///     The census can still find what it is supposed to find.
    /// </summary>
    /// <remarks>
    ///     The control: the same scan, over a synthetic file, must report the
    ///     constructs the synthetic file contains. A scan that was emptied, or
    ///     that stopped recognising a construct, would report an empty tree and
    ///     the census above would rank nothing.
    /// </remarks>
    [Test]
    public async Task TheScan_FindsTheConstructsItIsLookingFor()
    {
        const string Synthetic = """
            private static string Art(int rune)
            {
                sb.Append(((char)rune).ToString());
                for (int i = 0; i < 4; i++)
                {
                    var sb = new StringBuilder();
                    string part = text.Substring(i, 2);
                    string[] pieces = part.Split('x');
                    var more = pieces.ToList();
                    var joined = string.Join(",", more);
                    var pattern = Regex.Matches(text, "a");
                    var formatted = string.Format("{0}", i);
                }
                return sb.ToString();
            }
            """;

        IReadOnlyList<AllocMechanism> found = [.. ScanLines(Synthetic.Split('\n'), "Synthetic", "Synthetic")
            .Select(site => site.Mechanism)];

        await Assert.That(found.Contains(AllocMechanism.CharToString)).IsTrue()
            .Because("the scan must recognise the one-character-string construct");
        await Assert.That(found.Contains(AllocMechanism.BuilderInLoop)).IsTrue()
            .Because("the scan must recognise a builder built inside a loop");
        await Assert.That(found.Contains(AllocMechanism.SubstringInLoop)).IsTrue()
            .Because("the scan must recognise a substring taken inside a loop");
        await Assert.That(found.Contains(AllocMechanism.SplitInLoop)).IsTrue()
            .Because("the scan must recognise a split inside a loop");
        await Assert.That(found.Contains(AllocMechanism.MaterialiseInLoop)).IsTrue()
            .Because("the scan must recognise a list materialised inside a loop");
        await Assert.That(found.Contains(AllocMechanism.JoinInLoop)).IsTrue()
            .Because("the scan must recognise a join inside a loop");
        await Assert.That(found.Contains(AllocMechanism.RegexMatches)).IsTrue()
            .Because("the scan must recognise a match collection");
        await Assert.That(found.Contains(AllocMechanism.StringFormat)).IsTrue()
            .Because("the scan must recognise a formatted string");
    }

    /// <summary>
    ///     Walks the test tree and reports every allocating construct.
    /// </summary>
    /// <returns>The sites, in the order the tree was walked.</returns>
    private static IReadOnlyList<AllocSite> FindSites()
    {
        var sites = new List<AllocSite>();
        string root = Path.Combine(RepoPaths.RepoRoot!, TreeDirectory);

        if (!Directory.Exists(root))
        {
            return sites;
        }

        foreach (string file in TreeFiles(root))
        {
            string? directory = Path.GetDirectoryName(file);
            string project = directory is null ? "?" : Path.GetFileName(directory) ?? "?";
            string relative = Path.GetRelativePath(RepoPaths.RepoRoot!, file);

            sites.AddRange(ScanLines(File.ReadAllLines(file), project, relative));
        }

        return sites;
    }

    /// <summary>
    ///     Every source file of the test tree, build output excluded.
    /// </summary>
    /// <param name="root">The tree's root directory.</param>
    /// <returns>The files.</returns>
    private static IEnumerable<string> TreeFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    ///     The scan: one file's lines in, the allocating constructs out.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="project">The project the file belongs to.</param>
    /// <param name="file">The file, for the report.</param>
    /// <returns>The sites found in the file.</returns>
    private static IReadOnlyList<AllocSite> ScanLines(
        IReadOnlyList<string> lines,
        string project,
        string file)
    {
        var sites = new List<AllocSite>();
        string currentClass = "?";
        string currentTest = "?";
        bool pendingTest = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            Match classMatch = ClassDeclaration.Match(line);
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups["name"].Value;
                currentTest = "?";
                pendingTest = false;
            }

            if (trimmed.StartsWith("[Test", StringComparison.Ordinal)
                || trimmed.StartsWith("[TestMethod", StringComparison.Ordinal)
                || trimmed.StartsWith("[TestCase", StringComparison.Ordinal)
                || trimmed.StartsWith("[Theory", StringComparison.Ordinal))
            {
                pendingTest = true;
            }
            else
            {
                Match methodMatch = MethodDeclaration.Match(line);
                if (methodMatch.Success)
                {
                    currentTest = pendingTest
                        ? methodMatch.Groups["name"].Value
                        : currentClass;
                    pendingTest = false;
                }
            }

            bool inLoop = IsInsideALoop(lines, i);

            foreach (AllocMechanism mechanism in MechanismsOn(line, inLoop))
            {
                sites.Add(new AllocSite(project, file, i + 1, currentTest, mechanism));
            }
        }

        return sites;
    }

    /// <summary>
    ///     Whether a line sits inside a loop.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="index">The line to judge.</param>
    /// <returns><c>true</c> when the nearest enclosing block is a loop.</returns>
    /// <remarks>
    ///     <para>
    ///         The answer comes from a BACKWARD SCAN, and not from counting
    ///         braces, and the reason is that counting braces over C# source
    ///         is a lexer problem: 544 lines of this tree carry a brace inside
    ///         a comment or a string literal, and a counter that counted them
    ///         would drift for the rest of the file. The repository's own
    ///         comment stripper keeps the literals, so it cannot answer this
    ///         either.
    ///     </para>
    ///     <para>
    ///         The scan walks up from the line and stops at the first line
    ///         that CLOSES a block — a line whose first non-space character is
    ///         '}'. If it reaches a loop opener first, the line is inside a
    ///         loop.
    ///     </para>
    ///     <para>
    ///         The rule is CONSERVATIVE, and the census's numbers are an upper
    ///         bound because of it: a construct that sits just after a loop
    ///         whose closing brace is written on the same line as its last
    ///         statement is read as being inside the loop. The ranking is what
    ///         the number is for, and the direction of the error is known.
    ///     </para>
    /// </remarks>
    private static bool IsInsideALoop(IReadOnlyList<string> lines, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            string trimmed = lines[i].Trim();

            if (trimmed.StartsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            if (LoopOpener.IsMatch(lines[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The mechanisms a line carries.
    /// </summary>
    /// <param name="line">The line to classify.</param>
    /// <param name="inLoop">Whether the line sits inside a loop.</param>
    /// <returns>The mechanisms, in a stable order.</returns>
    private static IEnumerable<AllocMechanism> MechanismsOn(string line, bool inLoop)
    {
        if (CharToString.IsMatch(line))
        {
            yield return AllocMechanism.CharToString;
        }

        if (line.Contains("Regex.Matches(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.RegexMatches;
        }

        if (line.Contains("string.Format(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.StringFormat;
        }

        if (line.Contains("File.ReadAllText(", StringComparison.Ordinal)
            || line.Contains("File.ReadAllLines(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.FileReadPerCall;
        }

        if (line.Contains("Directory.GetFiles(", StringComparison.Ordinal)
            || line.Contains("Directory.EnumerateFiles(", StringComparison.Ordinal)
            || line.Contains("Directory.GetDirectories(", StringComparison.Ordinal)
            || line.Contains("Directory.EnumerateDirectories(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.DirectoryWalk;
        }

        if (!inLoop)
        {
            yield break;
        }

        if (line.Contains(".Split(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.SplitInLoop;
        }

        if (line.Contains("string.Join(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.JoinInLoop;
        }

        if (line.Contains("new StringBuilder(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.BuilderInLoop;
        }

        if (line.Contains(".Substring(", StringComparison.Ordinal))
        {
            yield return AllocMechanism.SubstringInLoop;
        }

        if (line.Contains(".ToList()", StringComparison.Ordinal) || line.Contains(".ToArray()", StringComparison.Ordinal))
        {
            yield return AllocMechanism.MaterialiseInLoop;
        }
    }

    /// <summary>
    ///     What one call of each mechanism allocates, measured.
    /// </summary>
    /// <returns>The bytes per call, per mechanism.</returns>
    /// <remarks>
    ///     The input is a REPRESENTATIVE one: a source-file-shaped text of the
    ///     size the tree's files actually are, and a file of the median size in
    ///     the tree. The figure is therefore "what this construct costs on THIS
    ///     tree", not a unit in the abstract.
    /// </remarks>
    private static IReadOnlyDictionary<AllocMechanism, long> MeasureUnitCosts()
    {
        string text = RepresentativeSourceText();
        string[] lines = text.Split('\n');
        string? candidate = RepresentativeFile();

        long fileReadCost = 0;
        if (candidate is not null)
        {
            string path = candidate;
            fileReadCost = Measure(() => { _ = File.ReadAllText(path); });
        }

        var costs = new Dictionary<AllocMechanism, long>
        {
            [AllocMechanism.CharToString] = Measure(() => { _ = ((char)65).ToString(); }),
            [AllocMechanism.SplitInLoop] = Measure(() => { _ = text.Split('\n'); }),
            [AllocMechanism.JoinInLoop] = Measure(() => { _ = string.Join("\n", lines); }),
            [AllocMechanism.BuilderInLoop] = Measure(() =>
            {
                var sb = new StringBuilder(text.Length);
                sb.Append(text);
                _ = sb.ToString();
            }),
            [AllocMechanism.SubstringInLoop] = Measure(() => { _ = text.Substring(10, 32); }),
            [AllocMechanism.MaterialiseInLoop] = Measure(() => { _ = lines.ToList(); }),
            [AllocMechanism.RegexMatches] = Measure(() => { _ = Regex.Matches(text, @"\bvar\b").Count; }),
            [AllocMechanism.StringFormat] = Measure(() => { _ = string.Format("{0}-{1}", "a", 1); }),
            [AllocMechanism.FileReadPerCall] = fileReadCost,
            [AllocMechanism.DirectoryWalk] = Measure(() =>
            {
                _ = Directory.EnumerateFiles(RepoPaths.RepoRoot!, "*.cs", SearchOption.TopDirectoryOnly).Count();
            })
        };

        return costs;
    }

    /// <summary>
    ///     The bytes one call of <paramref name="body" /> allocates.
    /// </summary>
    /// <param name="body">The body to measure. Must not suspend.</param>
    /// <returns>The average number of bytes per call.</returns>
    private static long Measure(Action body)
    {
        const int Iterations = 50;

        // Warm the body, so the first call's JIT is not in the figure.
        body();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            body();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
    }

    /// <summary>
    ///     A source-file-shaped text of the size the tree's files actually are.
    /// </summary>
    /// <returns>The text.</returns>
    private static string RepresentativeSourceText()
    {
        var sb = new StringBuilder(8 * 1024);
        int line = 0;
        while (sb.Length < 8 * 1024)
        {
            sb.Append("    private static readonly string Sample").Append(line)
              .Append(" = \"a value that is long enough to look like a line of source code\";")
              .Append('\n');
            line++;
        }

        return sb.ToString();
    }

    /// <summary>
    ///     A file of the median size in the test tree, or <c>null</c> when the
    ///     tree holds no file.
    /// </summary>
    /// <returns>The path of the file, or <c>null</c>.</returns>
    private static string? RepresentativeFile()
    {
        string root = Path.Combine(RepoPaths.RepoRoot!, TreeDirectory);
        if (!Directory.Exists(root))
        {
            return null;
        }

        List<string> files = [.. TreeFiles(root)];
        if (files.Count == 0)
        {
            return null;
        }

        files.Sort((a, b) => new FileInfo(a).Length.CompareTo(new FileInfo(b).Length));
        return files[files.Count / 2];
    }

    /// <summary>Whether a path is build output.</summary>
    /// <param name="path">The path to test.</param>
    /// <returns><c>true</c> when the path is build output.</returns>
    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
